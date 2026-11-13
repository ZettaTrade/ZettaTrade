namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Livery.Localization;
using Livery.Themes;
using SkiaSharp;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Resources;

/// <summary>
/// Trade tape (time and sales) widget drawing directly onto the Avalonia Skia
/// canvas via <see cref="ICustomDrawOperation"/> (the OrderBookWidget pattern,
/// no intermediate bitmaps or per-trade controls, so a burst feed stays in
/// frame budget). The raw trades stream arrives through <see cref="Trades"/>:
/// the widget itself accumulates a bounded ring buffer (a push past the
/// capacity evicts the oldest trade — memory never grows with the feed),
/// coalesces the per-trade invalidations and pauses accepting trades while
/// the pointer hovers over the tape.
/// </summary>
public class TradeTapeWidget : Control
{
    #region Constants

    /// <summary>Vertical space of one trade row; the layout receives it.</summary>
    private const double RowHeightPx = 14;

    /// <summary>
    /// Vertical space of the header band: the label line (the paused hint)
    /// rides the top and the column captions sit above the body's first row
    /// with real padding (T-179) — the two text lines never share a line.
    /// </summary>
    private const double HeaderHeightPx = 26;

    /// <summary>Horizontal padding of the columns.</summary>
    private const float PaddingPx = 8;

    private const float HeaderFontSizePx = 12;
    private const float ColumnHeaderFontSizePx = 10;
    private const float RowFontSizePx = 11;

    /// <summary>Text baseline of the band's label line (the paused hint).</summary>
    private const float HeaderBaselinePx = 11;

    /// <summary>
    /// Text baseline of the column captions inside the band: clear of the
    /// label line's descenders above and of the first row's ink below.
    /// </summary>
    private const float ColumnHeaderBaselinePx = 22;

    /// <summary>Text baseline offset inside a row (top Y + offset).</summary>
    private const float RowBaselineOffsetPx = 10;

    /// <summary>The widget takes a snapshot of these resources on each redraw.</summary>
    private const string NoDataMessageKey = "Tape.NoDataMessage";

    /// <summary>Resource keys published by ThemeManager; buys reuse the bullish
    /// candle color and sells the bearish one, the rest mirrors the chart.</summary>
    private const string BackgroundColorKey = "Background";
    private const string GridColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string BuyColorKey = "CandleUp";
    private const string SellColorKey = "CandleDown";

    #endregion

    #region Styled Properties

    /// <summary>
    /// StyledProperty for the trades stream the widget receives and buffers.
    /// </summary>
    public static readonly StyledProperty<IObservable<MarketTrade>?> TradesProperty =
        AvaloniaProperty.Register<TradeTapeWidget, IObservable<MarketTrade>?>(nameof(Trades));

    /// <summary>
    /// Gets or sets the hot trades stream (per symbol, switched by the symbol
    /// chain upstream); the widget subscribes on attach and while the property
    /// carries a stream. A trade of a new symbol clears the buffered rows —
    /// old-symbol trades never linger above the new symbol's tape.
    /// </summary>
    public IObservable<MarketTrade>? Trades
    {
        get => GetValue(TradesProperty);
        set => SetValue(TradesProperty, value);
    }

    #endregion

    #region Fields & Properties

    // Theme-agnostic fallback colors: the built-in dark set, named so the
    // literals stay out of inline code; the live colors are refreshed from
    // the application resources on every snapshot (the book's convention).
    private static readonly SKColor DefaultBackgroundColor = new(20, 20, 30);
    private static readonly SKColor DefaultGridColor = new(40, 40, 50);
    private static readonly SKColor DefaultTextColor = new(200, 200, 200);
    private static readonly SKColor DefaultBuyColor = new(34, 197, 94);
    private static readonly SKColor DefaultSellColor = new(239, 68, 68);

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private SKColor _gridColor = DefaultGridColor;
    private SKColor _textColor = DefaultTextColor;
    private SKColor _buyColor = DefaultBuyColor;
    private SKColor _sellColor = DefaultSellColor;

    /// <summary>Subscription of the trades stream; swapped on a Trades change, disposed on detach.</summary>
    private IDisposable? _tradesSubscription;

    /// <summary>The stream the live subscription is bound to (a rebind with the same instance is a no-op).</summary>
    private IObservable<MarketTrade>? _subscribedSource;

    /// <summary>Subscription of the theme-changed stream; disposed on detach.</summary>
    private IDisposable? _themeChangeSubscription;

    /// <summary>Subscription of the language-changed stream; disposed on detach.</summary>
    private IDisposable? _languageChangeSubscription;

    /// <summary>
    /// Trades waiting for their frame: stream producers (any thread) enqueue
    /// here, the render path drains on the user-interface thread. Draining
    /// happens every frame regardless of the pause, so even a paused tape
    /// keeps this queue frame-bounded in memory.
    /// </summary>
    private readonly ConcurrentQueue<MarketTrade> _pending = new();

    /// <summary>
    /// The tape's bounded history; a push past the capacity evicts the oldest
    /// trade, so a burst feed can never grow the panel's memory.
    /// </summary>
    private readonly TradeTapeRingBuffer _ring = new(TradeTapeRingBuffer.DefaultCapacity);

    /// <summary>The reusable newest-first copy the render plans a page from.</summary>
    private readonly List<MarketTrade> _newestFirst = new();

    /// <summary>The normalized symbol the buffered rows belong to; a different symbol's trade clears first.</summary>
    private string? _bufferedSymbol;

    /// <summary>Whether the pointer sits on the tape: a paused tape keeps the rows it froze with.</summary>
    private bool _isPointerInside;

    /// <summary>Coalescing flag of the queued invalidation (non-zero while one is in flight).</summary>
    private long _invalidationScheduled;

    #endregion

    #region Constructor

    static TradeTapeWidget()
    {
        // A new stream (the binding's switched feed) rebinds the subscription;
        // the attach path rebinds it too. Both funnels through the same
        // idempotent rebind below.
        TradesProperty.Changed.AddClassHandler<TradeTapeWidget>((x, _) => x.RebindTrades());
    }

    public TradeTapeWidget()
    {
        // A plain Avalonia Control is hit-tested only through its children:
        // the drawn surface (custom draw operation) stays input-invisible.
        // A transparent child rectangle gives the whole bounds a hit-test
        // surface, so pointer enter/leave — the pause switch — reaches this
        // widget.
        var hitSurface = new Rectangle { Fill = Brushes.Transparent };
        hitSurface.Bind(
            Rectangle.WidthProperty,
            this.GetObservable(BoundsProperty).Select(b => b.Width));
        hitSurface.Bind(Rectangle.HeightProperty, this.GetObservable(BoundsProperty).Select(b => b.Height));
        VisualChildren.Add(hitSurface);

        ClipToBounds = true;
    }

    #endregion

    #region Stream Subscription

    /// <summary>
    /// Bind the subscription to the current <see cref="Trades"/> value unless
    /// it is the one already bound: attach, a property change and a detach
    /// all funnel here. A stream failure (releasable upstream — the VM's
    /// guard already logs it) drops only the stale pending trades; the
    /// buffered rows stay the tape the user last saw.
    /// </summary>
    private void RebindTrades()
    {
        var source = Trades;
        if (ReferenceEquals(source, _subscribedSource))
        {
            return;
        }

        _tradesSubscription?.Dispose();
        _tradesSubscription = null;
        _subscribedSource = source;

        if (source is not null)
        {
            _tradesSubscription = source.Subscribe(
                OnTrade,
                _ => _pending.Clear());
        }
    }

    /// <summary>
    /// One trade arrived (any stream thread): bucket it for the next frame
    /// and coalesce the invalidation.
    /// </summary>
    private void OnTrade(MarketTrade trade)
    {
        _pending.Enqueue(trade);
        ScheduleInvalidation();
    }

    #endregion

    #region Accessibility

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new TradeTapeAutomationPeer(this);

    /// <summary>
    /// The tape's accessibility peer (T-110): the widget is a Skia canvas
    /// whose drawing the base Control's default peer hides from the
    /// assistive tree, so this one restores visibility and names the tape
    /// for the symbol the buffered rows belong to.
    /// </summary>
    private sealed class TradeTapeAutomationPeer(TradeTapeWidget owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            var attached = base.GetNameCore();
            if (!string.IsNullOrWhiteSpace(attached))
            {
                return attached;
            }

            var (empty, withSymbol) = WidgetAccessibilityNames.TapeTemplates();
            var symbol = owner._bufferedSymbol;

            return WidgetAccessibilityNames.SymbolRoleName(
                empty,
                withSymbol,
                !string.IsNullOrWhiteSpace(symbol),
                symbol);
        }
    }

    #endregion

    #region Theme & Language Tracking

    /// <summary>
    /// While attached, re-render on every theme or language change (the
    /// book's convention): the draw operation takes a snapshot per render —
    /// RefreshThemeColors re-reads the resources and the page layout
    /// re-resolves the localized column headers — so invalidating is all it
    /// takes to pick up new colors and header names. Without this, a theme
    /// or language switch left the drawn tape stale until the next trade
    /// arrived — indefinitely while disconnected.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (ThemeManager.IsInitialized && _themeChangeSubscription is null)
        {
            _themeChangeSubscription = ThemeManager.Instance.ThemeChanged.Subscribe(
                _ => ScheduleInvalidation());
        }

        if (LocalizationManager.IsInitialized && _languageChangeSubscription is null)
        {
            _languageChangeSubscription = LocalizationManager.Instance.LanguageChanged.Subscribe(
                _ => ScheduleInvalidation());
        }

        RebindTrades();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _themeChangeSubscription?.Dispose();
        _themeChangeSubscription = null;
        _languageChangeSubscription?.Dispose();
        _languageChangeSubscription = null;

        _tradesSubscription?.Dispose();
        _tradesSubscription = null;
        _subscribedSource = null;
    }

    #endregion

    #region Pause-on-hover

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerInside = true;
        ScheduleInvalidation();
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerInside = false;
        ScheduleInvalidation();
    }

    #endregion

    #region Clearing

    /// <summary>
    /// Clear the tape: the panel's clear button empties the buffered rows
    /// (the button's wiring is a view concern — the buffered tape lives
    /// entirely in this widget).
    /// </summary>
    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ClearCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ClearCore);
        }
    }

    private void ClearCore()
    {
        _pending.Clear();
        _ring.Clear();
        _bufferedSymbol = null;
        ScheduleInvalidation();
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

        // The draw operation runs later, on the render thread: it must only
        // see an immutable snapshot, never the live ring copy the next frame
        // rebuilds. The snapshot rides the pure layout plan (see CreateSnapshot).
        context.Custom(new TradeTapeDrawOperation(
            new Rect(size),
            CreateSnapshot()));
    }

    /// <summary>
    /// Plans the page (pure layout) and copies the theme colors so the render
    /// thread works on its own data. Draining first: whatever arrived since
    /// the last frame joins the ring here (a paused tape drains without
    /// accepting, so the pending queue stays frame-bounded even then).
    /// </summary>
    private TradeTapeSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        DrainPendingTrades();
        _ring.CopyNewestFirst(_newestFirst, VisibleRowCount());
        var page = TradeTapePageLayout.Build(
            _newestFirst,
            Bounds.Size.Height,
            RowHeightPx,
            HeaderHeightPx,
            _isPointerInside);

        return new TradeTapeSnapshot(
            page,
            _backgroundColor,
            _gridColor,
            _textColor,
            _buyColor,
            _sellColor);
    }

    /// <summary>
    /// The rows the current height can show (the same constants the layout
    /// plans with), bounding the copy the frame performs.
    /// </summary>
    private int VisibleRowCount()
    {
        var bodyHeight = Math.Max(0, Bounds.Size.Height - HeaderHeightPx);
        return Math.Max(1, (int)(bodyHeight / RowHeightPx));
    }

    /// <summary>
    /// Drain the pending queue into the ring on the user-interface thread.
    /// Trades of a new symbol clear the buffered rows first — a switched
    /// feed never leaves the previous symbol's trades above the new one's.
    /// </summary>
    private void DrainPendingTrades()
    {
        while (_pending.TryDequeue(out var trade))
        {
            var symbol = trade.Symbol.Normalized;
            if (symbol != _bufferedSymbol)
            {
                _ring.Clear();
                _bufferedSymbol = symbol;
            }

            _ring.Push(trade);
        }
    }

    private sealed record TradeTapeSnapshot(
        TradeTapePage Page,
        SKColor Background,
        SKColor Grid,
        SKColor Text,
        SKColor Buy,
        SKColor Sell);

    /// <summary>
    /// Refresh the draw colors from the theme resources published by ThemeManager
    /// (Application.Current.Resources); falls back to the dark-theme defaults.
    /// </summary>
    private void RefreshThemeColors()
    {
        _backgroundColor = ResourceColor(BackgroundColorKey, _backgroundColor);
        _gridColor = ResourceColor(GridColorKey, _gridColor);
        _textColor = ResourceColor(TextColorKey, _textColor);
        _buyColor = ResourceColor(BuyColorKey, _buyColor);
        _sellColor = ResourceColor(SellColorKey, _sellColor);
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

    /// <summary>
    /// Queue one coalesced invalidation: trades arrive on stream threads, so
    /// a book-style Post per trade would line up an operation per trade; the
    /// flag collapses the burst into one post per frame. The reset rides the
    /// posted body, so a trade racing in after the reset re-arms the next
    /// post instead of being lost.
    /// </summary>
    private void ScheduleInvalidation()
    {
        if (Interlocked.Exchange(ref _invalidationScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                InvalidateVisual();
                Volatile.Write(ref _invalidationScheduled, 0);
            },
            DispatcherPriority.Normal);
    }

    #endregion

    /// <summary>
    /// Draws the planned page straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class TradeTapeDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TradeTapeSnapshot _snapshot;

        public TradeTapeDrawOperation(Rect bounds, TradeTapeSnapshot snapshot)
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
            // The paints and font are one set per frame — never per row — so
            // a full-height tape still draws with a fixed allocation count.
            var width = (float)_bounds.Width;
            var height = (float)_bounds.Height;

            using var textPaint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var buyPaint = new SKPaint
            {
                Color = _snapshot.Buy,
                IsAntialias = true,
            };

            using var sellPaint = new SKPaint
            {
                Color = _snapshot.Sell,
                IsAntialias = true,
            };

            using (var backgroundPaint = new SKPaint { Color = _snapshot.Background, Style = SKPaintStyle.Fill })
            {
                canvas.ClipRect(SKRect.Create(width, height));
                canvas.DrawRect(SKRect.Create(width, height), backgroundPaint);
            }

            var page = _snapshot.Page;
            if (page.Rows.Count == 0)
            {
                DrawNoDataMessage(canvas, width, height, textPaint);
                return;
            }

            using var font = new SKFont(SKTypeface.Default, RowFontSizePx);

            if (page.PausedHint is { } hint)
            {
                using var headerPaint = new SKPaint
                {
                    Color = _snapshot.Text,
                    IsAntialias = true,
                };

                using var headerFont = new SKFont(SKTypeface.Default, HeaderFontSizePx);

                canvas.DrawText(hint, width - PaddingPx, HeaderBaselinePx, SKTextAlign.Right, headerFont, headerPaint);
            }

            using var columnFont = new SKFont(SKTypeface.Default, ColumnHeaderFontSizePx);
            canvas.DrawText(page.TimeHeader, PaddingPx, ColumnHeaderBaselinePx, SKTextAlign.Left, columnFont, textPaint);
            canvas.DrawText(page.PriceHeader, width / 2, ColumnHeaderBaselinePx, SKTextAlign.Center, columnFont, textPaint);
            canvas.DrawText(page.SizeHeader, width - PaddingPx, ColumnHeaderBaselinePx, SKTextAlign.Right, columnFont, textPaint);

            foreach (var row in page.Rows)
            {
                // The tape's side is a color, and the neutral side (T-198 — the
                // wire served no aggressor) reads in the widget's own text
                // color: an unserved fact is never painted as a sell.
                var sidePaint = row.Side switch
                {
                    OrderSide.Buy => buyPaint,
                    OrderSide.Sell => sellPaint,
                    _ => textPaint,
                };

                canvas.DrawText(
                    row.TimeText,
                    PaddingPx,
                    row.Y + RowBaselineOffsetPx,
                    SKTextAlign.Left,
                    font,
                    textPaint);

                canvas.DrawText(
                    row.PriceText,
                    width / 2,
                    row.Y + RowBaselineOffsetPx,
                    SKTextAlign.Center,
                    font,
                    sidePaint);

                canvas.DrawText(
                    row.SizeText,
                    width - PaddingPx,
                    row.Y + RowBaselineOffsetPx,
                    SKTextAlign.Right,
                    font,
                    sidePaint);
            }
        }

        private void DrawNoDataMessage(
            SKCanvas canvas,
            float width,
            float height,
            SKPaint textPaint)
        {
            using var font = new SKFont(SKTypeface.Default, HeaderFontSizePx);

            canvas.DrawText(
                GetLocalizedNoDataText(),
                width / 2,
                height / 2,
                SKTextAlign.Center,
                font,
                textPaint);
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
