namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Resources;

/// <summary>
/// Candlestick chart control that draws directly onto the Avalonia Skia canvas
/// via <see cref="ICustomDrawOperation"/> (no intermediate bitmaps or pixel copies).
/// </summary>
public class CandlestickChart : Control
{
    #region Constants

    private const int MinVisibleBars = 20;
    private const int DefaultVisibleBars = 100;
    private const int VisibleBarsZoomStep = 10;
    private const int HorizontalGridLineCount = 5;
    private const int VerticalGridLineCount = 10;
    private const int PriceAxisLabelCount = 5;
    private const int TimeAxisLabelCount = 10;
    private const decimal PricePaddingRatio = 0.1m;
    private const float CandleBodyWidthRatio = 0.7f;
    private const float MinCandleBodyHeight = 1f;
    private const float NoDataFontSize = 16f;
    private const float AxisFontSize = 10f;
    private const int MinAxisLabelPaddingPx = 5;

    /// <summary>The chart takes a screenshot of these resources on each redraw.</summary>
    private const string NoDataMessageKey = "Chart.NoDataMessage";

    #endregion

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

    private int _visibleBars = DefaultVisibleBars;

    private SKColor _backgroundColor = new(20, 20, 30);
    private SKColor _bullishColor = new(34, 197, 94);
    private SKColor _bearishColor = new(239, 68, 68);
    private SKColor _gridColor = new(40, 40, 50);
    private SKColor _textColor = new(200, 200, 200);

    /// <summary>Chart background color. Redraws the chart on change.</summary>
    public SKColor BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            _backgroundColor = value;
            InvalidateVisual();
        }
    }

    /// <summary>Bullish candle color. Redraws the chart on change.</summary>
    public SKColor BullishColor
    {
        get => _bullishColor;
        set
        {
            _bullishColor = value;
            InvalidateVisual();
        }
    }

    /// <summary>Bearish candle color. Redraws the chart on change.</summary>
    public SKColor BearishColor
    {
        get => _bearishColor;
        set
        {
            _bearishColor = value;
            InvalidateVisual();
        }
    }

    /// <summary>Grid line color. Redraws the chart on change.</summary>
    public SKColor GridColor
    {
        get => _gridColor;
        set
        {
            _gridColor = value;
            InvalidateVisual();
        }
    }

    /// <summary>Text color. Redraws the chart on change.</summary>
    public SKColor TextColor
    {
        get => _textColor;
        set
        {
            _textColor = value;
            InvalidateVisual();
        }
    }

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
            var start = Math.Max(0, bars.Count - _visibleBars);
            var end = Math.Min(bars.Count, start + _visibleBars);

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

            var padding = priceRange * PricePaddingRatio;
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

            using var font = new SKFont(SKTypeface.Default, NoDataFontSize);

            var text = Strings.ResourceManager.GetString(
                NoDataMessageKey,
                CultureInfo.CurrentUICulture) ?? NoDataMessageKey;

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

            for (var i = 0; i <= HorizontalGridLineCount; i++)
            {
                var y = (float)(bounds.Top + (bounds.Height * i / HorizontalGridLineCount));
                canvas.DrawLine((float)bounds.Left, y, (float)bounds.Right, y, paint);
            }

            for (var i = 0; i <= VerticalGridLineCount; i++)
            {
                var x = (float)(bounds.Left + (bounds.Width * i / VerticalGridLineCount));
                canvas.DrawLine(x, (float)bounds.Top, x, (float)bounds.Bottom, paint);
            }
        }

        private void DrawCandles(SKCanvas canvas, Rect bounds, IReadOnlyList<Bar> bars, decimal minPrice, decimal priceRange)
        {
            var candleWidth = (float)(bounds.Width / bars.Count);
            var bodyWidth = candleWidth * CandleBodyWidthRatio;

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

            for (var i = 0; i < bars.Count; i++)
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

                var bodyTop = Math.Min(openY, closeY);
                var bodyHeight = Math.Abs(closeY - openY);

                if (bodyHeight < MinCandleBodyHeight)
                    bodyHeight = MinCandleBodyHeight;

                canvas.DrawRect(
                    x - (bodyWidth / 2),
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

            using var font = new SKFont(SKTypeface.Default, AxisFontSize);

            for (var i = 0; i <= PriceAxisLabelCount; i++)
            {
                var price = minPrice + ((maxPrice - minPrice) * i / PriceAxisLabelCount);
                var y = (float)(bounds.Bottom - (bounds.Height * i / PriceAxisLabelCount));

                var text = price.ToString("F2");

                // Right-aligned at the chart edge.
                canvas.DrawText(text, (float)bounds.Right - MinAxisLabelPaddingPx, y + MinAxisLabelPaddingPx, SKTextAlign.Right, font, paint);
            }

            var timeSteps = Math.Min(TimeAxisLabelCount, bars.Count);
            for (var i = 0; i < timeSteps; i++)
            {
                var barIndex = i * bars.Count / timeSteps;
                if (barIndex >= bars.Count)
                    break;

                var bar = bars[barIndex];
                var x = (float)(bounds.Left + (bounds.Width * i / timeSteps));

                var text = bar.Time.ToString("HH:mm");

                canvas.DrawText(text, x, (float)bounds.Bottom - MinAxisLabelPaddingPx, SKTextAlign.Center, font, paint);
            }
        }
    }

    #endregion

    #region Interaction

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        var barCount = Bars?.Count ?? 0;

        if (delta > 0)
        {
            _visibleBars = Math.Max(MinVisibleBars, _visibleBars - VisibleBarsZoomStep);
        }
        else
        {
            _visibleBars = Math.Min(Math.Max(MinVisibleBars, barCount), _visibleBars + VisibleBarsZoomStep);
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    #endregion
}
