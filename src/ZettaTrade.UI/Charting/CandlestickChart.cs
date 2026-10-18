namespace ZettaTrade.UI.Charting;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using SkiaSharp;
using ZettaTrade.Core.Entities;

/// <summary>
/// Candlestick chart control that draws directly onto the Avalonia Skia canvas
/// via <see cref="ICustomDrawOperation"/> (no intermediate bitmaps or pixel copies).
/// </summary>
public class CandlestickChart : Control
{
    #region Styled Properties

    /// <summary>
    /// StyledProperty for Bars collection.
    /// </summary>
    public static readonly StyledProperty<ObservableCollection<Bar>?> BarsProperty =
        AvaloniaProperty.Register<CandlestickChart, ObservableCollection<Bar>?>(nameof(Bars));

    /// <summary>
    /// Gets or sets the collection of bars to render.
    /// </summary>
    public ObservableCollection<Bar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    #endregion

    #region Fields & Properties

    private int _visibleBars = 100;
    private int _scrollOffset;

    /// <summary>Chart background color.</summary>
    public SKColor BackgroundColor { get; set; } = new SKColor(20, 20, 30);

    /// <summary>Bullish candle color.</summary>
    public SKColor BullishColor { get; set; } = new SKColor(34, 197, 94);

    /// <summary>Bearish candle color.</summary>
    public SKColor BearishColor { get; set; } = new SKColor(239, 68, 68);

    /// <summary>Grid line color.</summary>
    public SKColor GridColor { get; set; } = new SKColor(40, 40, 50);

    /// <summary>Text color.</summary>
    public SKColor TextColor { get; set; } = new SKColor(200, 200, 200);

    #endregion

    #region Constructor

    static CandlestickChart()
    {
        BarsProperty.Changed.AddClassHandler<CandlestickChart>((x, e) => x.OnBarsPropertyChanged(e));
    }

    public CandlestickChart()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    #endregion

    #region Property Change Handlers

    private void OnBarsPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is ObservableCollection<Bar> oldColl)
        {
            oldColl.CollectionChanged -= OnBarsCollectionChanged;
        }

        if (e.NewValue is ObservableCollection<Bar> newColl)
        {
            newColl.CollectionChanged += OnBarsCollectionChanged;
        }

        _scrollOffset = 0;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private void OnBarsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(InvalidateVisual);
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

        // The draw operation runs later, on the render thread: it must only see an immutable snapshot,
        // never the live (UI-thread) Bars collection.
        context.Custom(new CandlestickDrawOperation(new Rect(size), CreateSnapshot()));
    }

    /// <summary>
    /// Copies only the visible window of bars (and the colors) so the render thread works on its own data.
    /// Assumes <see cref="Bar"/> instances are not mutated after they are added to the collection.
    /// </summary>
    private ChartSnapshot CreateSnapshot()
    {
        Bar[] visible = [];
        var bars = Bars;

        if (bars is { Count: > 0 })
        {
            var start = System.Math.Max(0, bars.Count - _visibleBars - _scrollOffset);
            var end = System.Math.Min(bars.Count, start + _visibleBars);

            visible = new Bar[end - start];
            for (var i = 0; i < visible.Length; i++)
                visible[i] = bars[start + i];
        }

        return new ChartSnapshot(visible, BackgroundColor, BullishColor, BearishColor, GridColor, TextColor);
    }

    private sealed record ChartSnapshot(
        Bar[] VisibleBars,
        SKColor Background,
        SKColor Bullish,
        SKColor Bearish,
        SKColor Grid,
        SKColor Text);

    /// <summary>
    /// Draws the chart straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class CandlestickDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly ChartSnapshot _snapshot;

        public CandlestickDrawOperation(Rect bounds, ChartSnapshot snapshot)
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

            var visibleBars = _snapshot.VisibleBars;

            if (visibleBars.Length == 0)
            {
                DrawNoDataMessage(canvas, width, height);
                return;
            }

            var minPrice = visibleBars.Min(b => b.Low);
            var maxPrice = visibleBars.Max(b => b.High);
            var priceRange = maxPrice - minPrice;

            if (priceRange == 0)
                priceRange = 1;

            var padding = priceRange * 0.1m;
            minPrice -= padding;
            maxPrice += padding;
            priceRange = maxPrice - minPrice;

            var bounds = new Rect(0, 0, width, height);
            DrawGrid(canvas, bounds);
            DrawCandles(canvas, bounds, visibleBars, minPrice, priceRange);
            DrawAxes(canvas, bounds, visibleBars, minPrice, maxPrice);
        }

        private void DrawNoDataMessage(SKCanvas canvas, double width, double height)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true
            };

            using var font = new SKFont(SKTypeface.Default, 16);

            // TODO: move to localization (Strings.resx) instead of a hardcoded string.
            var text = "No data available";

            canvas.DrawText(
                text,
                (float)width / 2,
                (float)height / 2,
                SKTextAlign.Center,
                font,
                paint);
        }

        private void DrawGrid(SKCanvas canvas, Rect bounds)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Grid,
                StrokeWidth = 1,
                IsAntialias = true
            };

            var gridLines = 5;
            for (int i = 0; i <= gridLines; i++)
            {
                var y = (float)(bounds.Top + (bounds.Height * i / gridLines));
                canvas.DrawLine((float)bounds.Left, y, (float)bounds.Right, y, paint);
            }

            var verticalLines = 10;
            for (int i = 0; i <= verticalLines; i++)
            {
                var x = (float)(bounds.Left + (bounds.Width * i / verticalLines));
                canvas.DrawLine(x, (float)bounds.Top, x, (float)bounds.Bottom, paint);
            }
        }

        private void DrawCandles(SKCanvas canvas, Rect bounds, IReadOnlyList<Bar> bars, decimal minPrice, decimal priceRange)
        {
            var candleWidth = (float)(bounds.Width / bars.Count);
            var bodyWidth = candleWidth * 0.7f;

            using var bullishPaint = new SKPaint
            {
                Color = _snapshot.Bullish,
                IsAntialias = true
            };

            using var bearishPaint = new SKPaint
            {
                Color = _snapshot.Bearish,
                IsAntialias = true
            };

            for (int i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var x = (float)(bounds.Left + (i * candleWidth) + (candleWidth / 2));

                var isBullish = bar.Close >= bar.Open;
                var paint = isBullish ? bullishPaint : bearishPaint;

                var openY = (float)(bounds.Bottom - (double)((bar.Open - minPrice) / priceRange) * bounds.Height);
                var closeY = (float)(bounds.Bottom - (double)((bar.Close - minPrice) / priceRange) * bounds.Height);
                var highY = (float)(bounds.Bottom - (double)((bar.High - minPrice) / priceRange) * bounds.Height);
                var lowY = (float)(bounds.Bottom - (double)((bar.Low - minPrice) / priceRange) * bounds.Height);

                canvas.DrawLine(x, highY, x, lowY, paint);

                var bodyTop = System.Math.Min(openY, closeY);
                var bodyHeight = System.Math.Abs(closeY - openY);

                if (bodyHeight < 1)
                    bodyHeight = 1;

                canvas.DrawRect(
                    x - bodyWidth / 2,
                    bodyTop,
                    bodyWidth,
                    bodyHeight,
                    paint);
            }
        }

        private void DrawAxes(SKCanvas canvas, Rect bounds, IReadOnlyList<Bar> bars, decimal minPrice, decimal maxPrice)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true
            };

            using var font = new SKFont(SKTypeface.Default, 10);

            var priceSteps = 5;
            for (int i = 0; i <= priceSteps; i++)
            {
                var price = minPrice + (maxPrice - minPrice) * i / priceSteps;
                var y = (float)(bounds.Bottom - (bounds.Height * i / priceSteps));

                var text = price.ToString("F2");

                // Right-aligned at the chart edge (replaces manual MeasureText offset).
                canvas.DrawText(text, (float)bounds.Right - 5, y + 5, SKTextAlign.Right, font, paint);
            }

            var timeSteps = System.Math.Min(10, bars.Count);
            for (int i = 0; i < timeSteps; i++)
            {
                var barIndex = i * bars.Count / timeSteps;
                if (barIndex >= bars.Count)
                    break;

                var bar = bars[barIndex];
                var x = (float)(bounds.Left + (bounds.Width * i / timeSteps));

                var text = bar.Time.ToString("HH:mm");

                canvas.DrawText(text, x, (float)bounds.Bottom - 5, SKTextAlign.Center, font, paint);
            }
        }
    }

    #endregion

    #region Interaction

    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        var barCount = Bars?.Count ?? 0;

        if (delta > 0)
        {
            _visibleBars = System.Math.Max(20, _visibleBars - 10);
        }
        else
        {
            _visibleBars = System.Math.Min(System.Math.Max(20, barCount), _visibleBars + 10);
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    #endregion
}
