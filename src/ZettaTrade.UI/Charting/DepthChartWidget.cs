namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Livery.Localization;
using Livery.Themes;
using SkiaSharp;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Resources;

/// <summary>
/// Depth chart widget drawing directly onto the Avalonia Skia canvas via
/// <see cref="ICustomDrawOperation"/> (the OrderBookWidget pattern, no
/// intermediate bitmaps or per-level controls, so 60 fps with 1000+ levels
/// stays in frame budget). The snapshot arrives through <see cref="Book"/>:
/// the same streamed property the ladder binds (no new transport), so both
/// components resolve the same served depth — the T-072 contract, see
/// <see cref="DepthChartLayout"/>'s remarks.
/// </summary>
public class DepthChartWidget : Control
{
    #region Constants

    /// <summary>Vertical space of one plotted band; the layout receives it.
    /// Mirrors the ladder's row height, so the two panels share the level
    /// pitch the resolved depth is displayed with.</summary>
    private const double BandHeightPx = 14;

    /// <summary>Font size of the mid price printed on the mid line.</summary>
    private const float MidLabelFontSizePx = 11;

    /// <summary>Floats the mid label just above the mid line.</summary>
    private const float MidLabelBaselineOffsetPx = 4;

    /// <summary>Alpha of the cumulative area fill (the ladder bar's alpha).</summary>
    private const byte AreaAlpha = 70;

    /// <summary>The widget takes a snapshot of these resources on each redraw.</summary>
    private const string NoDataMessageKey = "OrderBook.NoDataMessage";

    /// <summary>Resource keys published by ThemeManager; bids reuse the bullish
    /// candle color and asks the bearish one, the rest mirrors the chart.</summary>
    private const string BackgroundColorKey = "Background";
    private const string GridColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string BidColorKey = "CandleUp";
    private const string AskColorKey = "CandleDown";

    #endregion

    #region Styled Properties

    /// <summary>
    /// StyledProperty for the current order book snapshot.
    /// </summary>
    public static readonly StyledProperty<OrderBook?> BookProperty =
        AvaloniaProperty.Register<DepthChartWidget, OrderBook?>(nameof(Book));

    /// <summary>
    /// Gets or sets the order book to render; null renders the no-data state.
    /// The ladder's widget and this one bind the same property upstream, so
    /// every frame the two plans cut the same served snapshot.
    /// </summary>
    public OrderBook? Book
    {
        get => GetValue(BookProperty);
        set => SetValue(BookProperty, value);
    }

    #endregion

    #region Fields & Properties

    // Theme-agnostic fallback colors: the built-in dark set, named so the
    // literals stay out of inline code; the live colors are refreshed from
    // the application resources on every snapshot (the chart's convention).
    private static readonly SKColor DefaultBackgroundColor = new(20, 20, 30);
    private static readonly SKColor DefaultGridColor = new(40, 40, 50);
    private static readonly SKColor DefaultTextColor = new(200, 200, 200);
    private static readonly SKColor DefaultBidColor = new(34, 197, 94);
    private static readonly SKColor DefaultAskColor = new(239, 68, 68);

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private SKColor _gridColor = DefaultGridColor;
    private SKColor _textColor = DefaultTextColor;
    private SKColor _bidColor = DefaultBidColor;
    private SKColor _askColor = DefaultAskColor;

    /// <summary>Subscription of the theme-changed stream; disposed on detach.</summary>
    private IDisposable? _themeChangeSubscription;

    /// <summary>Subscription of the language-changed stream; disposed on detach.</summary>
    private IDisposable? _languageChangeSubscription;

    #endregion

    #region Constructor

    static DepthChartWidget()
    {
        BookProperty.Changed.AddClassHandler<DepthChartWidget>((x, _) =>
            Dispatcher.UIThread.Post(x.InvalidateVisual));
    }

    public DepthChartWidget()
    {
        ClipToBounds = true;
    }

    #endregion

    #region Accessibility

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new DepthChartAutomationPeer(this);

    /// <summary>
    /// The depth chart's accessibility peer (T-110): the widget is a Skia
    /// canvas whose drawing the base Control's default peer hides from the
    /// assistive tree, so this one restores visibility and names the depth
    /// curve for its current symbol (the book panel serves two widget
    /// panes; the peer keeps them distinguishable).
    /// </summary>
    private sealed class DepthChartAutomationPeer(DepthChartWidget owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            var attached = base.GetNameCore();
            if (!string.IsNullOrWhiteSpace(attached))
            {
                return attached;
            }

            var (empty, withSymbol) = WidgetAccessibilityNames.DepthTemplates();
            var book = owner.Book;

            return WidgetAccessibilityNames.SymbolRoleName(
                empty,
                withSymbol,
                book is not null,
                book?.Symbol.Normalized);
        }
    }

    #endregion

    #region Theme & Language Tracking

    /// <summary>
    /// While attached, re-render on every theme or language change (the
    /// book's convention): the draw operation takes a snapshot per render —
    /// RefreshThemeColors re-reads the resources and the no-data state
    /// re-resolves its localized text — so invalidating is all it takes to
    /// pick up new colors and wording. Without this, a theme or language
    /// switch left the drawn shape stale until the next Book snapshot
    /// arrived — indefinitely while disconnected.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (ThemeManager.IsInitialized && _themeChangeSubscription is null)
        {
            _themeChangeSubscription = ThemeManager.Instance.ThemeChanged.Subscribe(
                _ => Dispatcher.UIThread.Post(InvalidateVisual));
        }

        if (LocalizationManager.IsInitialized && _languageChangeSubscription is null)
        {
            _languageChangeSubscription = LocalizationManager.Instance.LanguageChanged.Subscribe(
                _ => Dispatcher.UIThread.Post(InvalidateVisual));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _themeChangeSubscription?.Dispose();
        _themeChangeSubscription = null;
        _languageChangeSubscription?.Dispose();
        _languageChangeSubscription = null;
    }

    #endregion

    #region Rendering

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
        {
            return;
        }

        // The draw operation runs later, on the render thread: it must only see
        // an immutable snapshot, never the live UI-thread Book reference.
        context.Custom(new DepthChartDrawOperation(
            new Rect(size),
            CreateSnapshot()));
    }

    /// <summary>
    /// Plans the page (pure layout) and copies the theme colors so the render
    /// thread works on its own data; a missing book yields a no-data page.
    /// No inner header: the host composes the caption in the XAML row above
    /// (the ladder panel's title pattern), so the header inset stays zero.
    /// </summary>
    private DepthChartSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        var book = Book;
        DepthChartPage? page = book is null
            ? null
            : DepthChartLayout.Build(
                book,
                Bounds.Size.Width,
                Bounds.Size.Height,
                headerHeight: 0,
                BandHeightPx);

        return new DepthChartSnapshot(page, _backgroundColor, _gridColor, _textColor, _bidColor, _askColor);
    }

    private sealed record DepthChartSnapshot(
        DepthChartPage? Page,
        SKColor Background,
        SKColor Grid,
        SKColor Text,
        SKColor Bid,
        SKColor Ask);

    /// <summary>
    /// Refresh the draw colors from the theme resources published by ThemeManager
    /// (Application.Current.Resources); falls back to the dark-theme defaults.
    /// </summary>
    private void RefreshThemeColors()
    {
        _backgroundColor = ResourceColor(BackgroundColorKey, _backgroundColor);
        _gridColor = ResourceColor(GridColorKey, _gridColor);
        _textColor = ResourceColor(TextColorKey, _textColor);
        _bidColor = ResourceColor(BidColorKey, _bidColor);
        _askColor = ResourceColor(AskColorKey, _askColor);
    }

    private static SKColor ResourceColor(string key, SKColor fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, ThemeVariant.Default, out var value) == true &&
            value is Color color)
        {
            return new SKColor(color.R, color.G, color.B, color.A);
        }

        return fallback;
    }

    #endregion

    /// <summary>
    /// Draws the planned page straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class DepthChartDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly DepthChartSnapshot _snapshot;

        public DepthChartDrawOperation(Rect bounds, DepthChartSnapshot snapshot)
        {
            _bounds = bounds;
            _snapshot = snapshot;
        }

        public Rect Bounds => _bounds;

        public void Dispose()
        {
        }

        // Always redraw; the snapshot changes on nearly every invalidation anyway.
        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            // Null when the current rendering backend is not Skia: nothing to draw then.
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            try
            {
                Draw(canvas);
            }
            finally
            {
                canvas.Restore();
            }
        }

        private void Draw(SKCanvas canvas)
        {
            var width = (float)_bounds.Width;
            var height = (float)_bounds.Height;

            // Do NOT use canvas.Clear(): the canvas is shared with the whole window.
            var area = SKRect.Create(width, height);
            canvas.ClipRect(area);

            using (var backgroundPaint = new SKPaint { Color = _snapshot.Background, Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(area, backgroundPaint);
            }

            if (_snapshot.Page is not { } page ||
                (page.Bids.Count == 0 && page.Asks.Count == 0))
            {
                DrawNoDataMessage(canvas, width, height);
                return;
            }

            DrawArea(canvas, page, page.Asks, isBelowMid: false, _snapshot.Ask);
            DrawArea(canvas, page, page.Bids, isBelowMid: true, _snapshot.Bid);
            DrawMidLine(canvas, page, width);
            DrawMidLabel(canvas, page, width);
        }

        /// <summary>
        /// One side's cumulative staircase: the planned slice geometry builds
        /// the whole polygon (fill paints and the two paths are per frame,
        /// never per band). Both sides mirror each other: the fill steps
        /// outward from the mid line until it spans the full interior.
        /// </summary>
        private void DrawArea(
            SKCanvas canvas,
            DepthChartPage page,
            IReadOnlyList<DepthSlice> slices,
            bool isBelowMid,
            SKColor color)
        {
            if (slices.Count == 0)
            {
                return;
            }

            using var fillPaint = new SKPaint
            {
                Color = color.WithAlpha(AreaAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };

            // The outgoing edge flips with the side: bids step downward from
            // the mid line, asks upward; the planned slice Y already carries
            // each band's step edge.
            var edgeY = isBelowMid ? page.SliceHeightPx : -page.SliceHeightPx;

            using var builder = new SKPathBuilder();
            builder.MoveTo(page.LeftX, page.MidY);
            foreach (var slice in slices)
            {
                builder.LineTo(slice.X, slice.Y);
                builder.LineTo(slice.X, slice.Y + edgeY);
            }

            builder.LineTo(page.LeftX, page.MidY + (edgeY * slices.Count));
            builder.Close();

            using var path = builder.Detach();
            canvas.DrawPath(path, fillPaint);
        }

        private void DrawMidLine(SKCanvas canvas, DepthChartPage page, float width)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Grid,
                StrokeWidth = 1,
                IsAntialias = true,
            };

            canvas.DrawLine(0, page.MidY, width, page.MidY, paint);
        }

        private void DrawMidLabel(SKCanvas canvas, DepthChartPage page, float width)
        {
            if (page.MidLabel is not { } label)
            {
                return;
            }

            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, MidLabelFontSizePx);

            canvas.DrawText(
                label,
                width / 2,
                page.MidY - MidLabelBaselineOffsetPx,
                SKTextAlign.Center,
                font,
                paint);
        }

        private void DrawNoDataMessage(SKCanvas canvas, float width, float height)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, MidLabelFontSizePx);

            canvas.DrawText(
                GetLocalizedNoDataText(),
                width / 2,
                height / 2,
                SKTextAlign.Center,
                font,
                paint);
        }

        private static string GetLocalizedNoDataText()
        {
            if (LocalizationManager.IsInitialized)
            {
                return LocalizationManager.Instance.GetString(NoDataMessageKey);
            }

            // Design mode / manager not wired yet: raw resource lookup instead.
            return Strings.ResourceManager.GetString(
                NoDataMessageKey,
                CultureInfo.CurrentUICulture) ?? NoDataMessageKey;
        }
    }
}
