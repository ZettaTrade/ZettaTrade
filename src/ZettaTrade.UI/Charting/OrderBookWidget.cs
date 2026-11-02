namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Localization;
using ZettaTrade.UI.Resources;
using ZettaTrade.UI.Themes;

/// <summary>
/// Order book depth widget drawing directly onto the Avalonia Skia canvas
/// via <see cref="ICustomDrawOperation"/> (the CandlestickChart pattern, no
/// intermediate bitmaps or per-level controls, so 60 fps with 1000+ levels
/// stays in frame budget). The snapshot arrives through <see cref="Book"/>.
/// </summary>
public class OrderBookWidget : Control
{
    #region Constants

    /// <summary>Vertical space of one level row; the layout receives it.</summary>
    private const double RowHeightPx = 14;

    /// <summary>Vertical space of the symbol/spread header band.</summary>
    private const double HeaderHeightPx = 22;

    /// <summary>Horizontal padding of the columns.</summary>
    private const float PaddingPx = 8;

    private const float HeaderFontSizePx = 12;
    private const float ColumnHeaderFontSizePx = 10;
    private const float RowFontSizePx = 11;

    /// <summary>Text baseline offset inside a row (top Y + offset).</summary>
    private const float RowBaselineOffsetPx = 10;

    /// <summary>Alpha of the cumulative-volume bar fill.</summary>
    private const byte BarAlpha = 70;

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
        AvaloniaProperty.Register<OrderBookWidget, OrderBook?>(nameof(Book));

    /// <summary>
    /// Gets or sets the order book to render; null renders the no-data state.
    /// </summary>
    public OrderBook? Book
    {
        get => GetValue(BookProperty);
        set => SetValue(BookProperty, value);
    }

    #endregion

    #region Fields & Properties

    // Defaults match the built-in dark theme; the live colors are refreshed
    // from application resources on every snapshot (the chart's convention).
    private SKColor _backgroundColor = new(20, 20, 30);
    private SKColor _gridColor = new(40, 40, 50);
    private SKColor _textColor = new(200, 200, 200);
    private SKColor _bidColor = new(34, 197, 94);
    private SKColor _askColor = new(239, 68, 68);

    /// <summary>
    /// The level-presses stream sink (hot; see <see cref="LevelClicked"/>).
    /// </summary>
    private readonly Subject<OrderBookLevelClickedEventArgs> _levelClicked = new();

    /// <summary>
    /// The level presses stream, hot and fed on the UI thread: a pointer
    /// press on a book level publishes the side the user would trade AT that
    /// price (bids buy, asks sell) plus the level's price and the book's
    /// symbol. The routing decision (typically: prefill the New Order
    /// dialog) belongs to the owning window, not the widget.
    /// </summary>
    public IObservable<OrderBookLevelClickedEventArgs> LevelClicked => _levelClicked;

    #endregion

    #region Constructor

    static OrderBookWidget()
    {
        BookProperty.Changed.AddClassHandler<OrderBookWidget>((x, _) =>
            Dispatcher.UIThread.Post(x.InvalidateVisual));
    }

    public OrderBookWidget()
    {
        ClipToBounds = true;
    }

    #endregion

    #region Rendering

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
            return;

        // The draw operation runs later, on the render thread: it must only see
        // an immutable snapshot, never the live UI-thread Book reference.
        context.Custom(new OrderBookDrawOperation(
            new Rect(size),
            CreateSnapshot()));
    }

    /// <summary>
    /// Plans the page (pure layout) and copies the theme colors so the render
    /// thread works on its own data; a missing book yields a no-data page.
    /// </summary>
    private OrderBookSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        var book = Book;
        OrderBookPage? page = book is null
            ? null
            : OrderBookPageLayout.Build(
                book,
                Bounds.Size.Height,
                RowHeightPx,
                HeaderHeightPx);

        return new OrderBookSnapshot(page, _backgroundColor, _gridColor, _textColor, _bidColor, _askColor);
    }

    private sealed record OrderBookSnapshot(
        OrderBookPage? Page,
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

    #region Interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        // The level under the cursor resolves against the CURRENT snapshot
        // (the same constants the render planned with: row height, header
        // height). No book, a header click or a press beyond the planned
        // rows stays unrouted.
        var position = e.GetPosition(this);
        var book = Book;
        var hit = OrderBookPageLayout.ResolveLevel(
            book,
            Bounds.Height,
            position.Y,
            RowHeightPx,
            HeaderHeightPx);
        if (book is not null && hit is { } level)
        {
            _levelClicked.OnNext(
                new OrderBookLevelClickedEventArgs(book.Symbol, level.Side, level.Price));
        }
    }

    #endregion

    /// <summary>
    /// Draws the planned page straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class OrderBookDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly OrderBookSnapshot _snapshot;

        public OrderBookDrawOperation(Rect bounds, OrderBookSnapshot snapshot)
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
                return;

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

            if (_snapshot.Page is not { } page)
            {
                DrawNoDataMessage(canvas, width, height);
                return;
            }

            DrawHeader(canvas, width, page);
            DrawColumnHeaders(canvas, width, page);
            DrawMidLine(canvas, width, page);
            DrawSide(canvas, width, page.Asks, _snapshot.Ask);
            DrawSide(canvas, width, page.Bids, _snapshot.Bid);
        }

        private void DrawHeader(SKCanvas canvas, float width, OrderBookPage page)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, HeaderFontSizePx);

            canvas.DrawText(page.SymbolLabel, PaddingPx, HeaderFontSizePx, SKTextAlign.Left, font, paint);

            if (page.SpreadText is { } spread)
            {
                canvas.DrawText(spread, width - PaddingPx, HeaderFontSizePx, SKTextAlign.Right, font, paint);
            }
        }

        private void DrawColumnHeaders(SKCanvas canvas, float width, OrderBookPage page)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, ColumnHeaderFontSizePx);

            canvas.DrawText(page.PriceHeader, PaddingPx, (float)HeaderHeightPx, SKTextAlign.Left, font, paint);
            canvas.DrawText(page.CumulativeHeader, width - PaddingPx, (float)HeaderHeightPx, SKTextAlign.Right, font, paint);
        }

        private void DrawMidLine(SKCanvas canvas, float width, OrderBookPage page)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Grid,
                StrokeWidth = 1,
                IsAntialias = true,
            };

            canvas.DrawLine(0, page.MidY, width, page.MidY, paint);
        }

        private void DrawSide(
            SKCanvas canvas,
            float width,
            IReadOnlyList<OrderBookDrawRow> rows,
            SKColor color)
        {
            using var barPaint = new SKPaint
            {
                Color = color.WithAlpha(BarAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };

            using var textPaint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, RowFontSizePx);

            var interior = width - (2 * PaddingPx);

            foreach (var row in rows)
            {
                var barWidth = interior * row.CumulativeFraction;

                canvas.DrawRect(
                    SKRect.Create(
                        width - PaddingPx - barWidth,
                        row.Y + 1,
                        barWidth,
                        (float)RowHeightPx - 2),
                    barPaint);

                canvas.DrawText(
                    row.PriceText,
                    PaddingPx,
                    row.Y + RowBaselineOffsetPx,
                    SKTextAlign.Left,
                    font,
                    textPaint);

                canvas.DrawText(
                    row.CumulativeText,
                    width - PaddingPx,
                    row.Y + RowBaselineOffsetPx,
                    SKTextAlign.Right,
                    font,
                    textPaint);
            }
        }

        private void DrawNoDataMessage(SKCanvas canvas, float width, float height)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, HeaderFontSizePx);

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

/// <summary>
/// A clicked order book level: the side the user would trade at that price
/// (a bid is bought at, an ask is sold at), the level's price and the book's
/// symbol that owns the level.
/// </summary>
public sealed class OrderBookLevelClickedEventArgs(Symbol symbol, OrderSide side, decimal price) : EventArgs
{
    /// <summary>The book's symbol that owns the clicked level.</summary>
    public Symbol Symbol { get; } = symbol;

    /// <summary>The side the user would trade AT the clicked price.</summary>
    public OrderSide Side { get; } = side;

    /// <summary>The clicked level's price.</summary>
    public decimal Price { get; } = price;
}
