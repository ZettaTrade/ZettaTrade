namespace ZettaTrade.UI.Charting;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;

/// <summary>
/// Candlestick chart control using SkiaSharp for rendering.
/// </summary>
public class CandlestickChart : Control
{
    #region Properties

    private readonly List<Bar> _bars = new();
    private int _visibleBars = 100;
    private int _scrollOffset;
    private double _zoomLevel = 1.0;
    
    private SKCanvas? _canvas;
    private GRContext? _grContext;

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

    public CandlestickChart()
    {
        // Enable pointer events for interaction
        Focusable = true;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Set the data to display in the chart.
    /// </summary>
    public void SetData(IEnumerable<Bar> bars)
    {
        _bars.Clear();
        _bars.AddRange(bars);
        _scrollOffset = 0;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>
    /// Add a new bar to the chart.
    /// </summary>
    public void AddBar(Bar bar)
    {
        _bars.Add(bar);
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>
    /// Update the last bar in the chart.
    /// </summary>
    public void UpdateLastBar(Bar bar)
    {
        if (_bars.Count > 0)
        {
            _bars[_bars.Count - 1] = bar;
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    /// <summary>
    /// Clear all data from the chart.
    /// </summary>
    public void Clear()
    {
        _bars.Clear();
        _scrollOffset = 0;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        
        using var surface = CreateSurface(bounds);
        if (surface == null)
            return;

        _canvas = surface.Canvas;
        
        // Clear background
        _canvas.Clear(BackgroundColor);

        if (_bars.Count == 0)
        {
            DrawNoDataMessage();
            return;
        }

        // Calculate visible range
        var visibleStart = System.Math.Max(0, _bars.Count - _visibleBars - _scrollOffset);
        var visibleEnd = System.Math.Min(_bars.Count, visibleStart + _visibleBars);
        var visibleBars = _bars.Skip(visibleStart).Take(visibleEnd - visibleStart).ToList();

        if (visibleBars.Count == 0)
            return;

        // Calculate price range
        var minPrice = visibleBars.Min(b => b.Low);
        var maxPrice = visibleBars.Max(b => b.High);
        var priceRange = maxPrice - minPrice;
        var padding = priceRange * 0.1m;
        minPrice -= padding;
        maxPrice += padding;
        priceRange = maxPrice - minPrice;

        // Draw grid
        DrawGrid(bounds, minPrice, maxPrice);

        // Draw candles
        DrawCandles(bounds, visibleBars, minPrice, priceRange);

        // Draw axes
        DrawAxes(bounds, visibleBars, minPrice, maxPrice);

        // Flush and present
        _canvas.Flush();
        
        using var snapshot = surface.Snapshot();
        using var image = snapshot.ToAvaloniaImage();
        context.DrawImage(image, bounds);
    }

    private SKSurface? CreateSurface(Rect bounds)
    {
        var info = new SKImageInfo((int)bounds.Width, (int)bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        
        _grContext ??= GRContext.CreateGl();
        
        var renderTarget = new GRBackendRenderTarget(
            info.Width,
            info.Height,
            0,
            8,
            new GRGlFramebufferInfo(0, info.ColorType.ToGlSizedFormat()));

        return SKSurface.Create(_grContext, renderTarget, GRSurfaceOrigin.BottomLeft, info);
    }

    private void DrawNoDataMessage()
    {
        using var paint = new SKPaint
        {
            Color = TextColor,
            TextSize = 16,
            IsAntialias = true
        };

        var text = "No data available";
        var textWidth = paint.MeasureText(text);
        
        _canvas!.DrawText(
            text,
            (Bounds.Width - textWidth) / 2,
            Bounds.Height / 2,
            paint);
    }

    private void DrawGrid(Rect bounds, decimal minPrice, decimal maxPrice)
    {
        using var paint = new SKPaint
        {
            Color = GridColor,
            StrokeWidth = 1,
            IsAntialias = true
        };

        // Draw horizontal grid lines
        var gridLines = 5;
        for (int i = 0; i <= gridLines; i++)
        {
            var y = bounds.Top + (bounds.Height * i / gridLines);
            _canvas!.DrawLine(bounds.Left, y, bounds.Right, y, paint);
        }

        // Draw vertical grid lines
        var verticalLines = 10;
        for (int i = 0; i <= verticalLines; i++)
        {
            var x = bounds.Left + (bounds.Width * i / verticalLines);
            _canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
        }
    }

    private void DrawCandles(Rect bounds, List<Bar> bars, decimal minPrice, decimal priceRange)
    {
        var candleWidth = bounds.Width / bars.Count;
        var bodyWidth = candleWidth * 0.7f;

        using var bullishPaint = new SKPaint
        {
            Color = BullishColor,
            IsAntialias = true
        };

        using var bearishPaint = new SKPaint
        {
            Color = BearishColor,
            IsAntialias = true
        };

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = bounds.Left + (i * candleWidth) + (candleWidth / 2);
            
            var isBullish = bar.Close >= bar.Open;
            var paint = isBullish ? bullishPaint : bearishPaint;

            // Calculate Y positions
            var openY = bounds.Bottom - (float)((bar.Open - minPrice) / priceRange * bounds.Height);
            var closeY = bounds.Bottom - (float)((bar.Close - minPrice) / priceRange * bounds.Height);
            var highY = bounds.Bottom - (float)((bar.High - minPrice) / priceRange * bounds.Height);
            var lowY = bounds.Bottom - (float)((bar.Low - minPrice) / priceRange * bounds.Height);

            // Draw wick
            _canvas!.DrawLine(x, highY, x, lowY, paint);

            // Draw body
            var bodyTop = System.Math.Min(openY, closeY);
            var bodyHeight = System.Math.Abs(closeY - openY);
            
            if (bodyHeight < 1)
                bodyHeight = 1;

            _canvas.DrawRect(
                x - bodyWidth / 2,
                bodyTop,
                bodyWidth,
                bodyHeight,
                paint);
        }
    }

    private void DrawAxes(Rect bounds, List<Bar> bars, decimal minPrice, decimal maxPrice)
    {
        using var paint = new SKPaint
        {
            Color = TextColor,
            TextSize = 10,
            IsAntialias = true
        };

        // Draw price labels on right axis
        var priceSteps = 5;
        for (int i = 0; i <= priceSteps; i++)
        {
            var price = minPrice + (maxPrice - minPrice) * i / priceSteps;
            var y = bounds.Bottom - (bounds.Height * i / priceSteps);
            
            var text = price.ToString("F2");
            var textWidth = paint.MeasureText(text);
            
            _canvas!.DrawText(text, bounds.Right - textWidth - 5, y + 5, paint);
        }

        // Draw time labels on bottom axis
        var timeSteps = System.Math.Min(10, bars.Count);
        for (int i = 0; i < timeSteps; i++)
        {
            var barIndex = i * bars.Count / timeSteps;
            if (barIndex >= bars.Count)
                break;

            var bar = bars[barIndex];
            var x = bounds.Left + (bounds.Width * i / timeSteps);
            
            var text = bar.Time.ToString("HH:mm");
            var textWidth = paint.MeasureText(text);
            
            _canvas.DrawText(text, x - textWidth / 2, bounds.Bottom - 5, paint);
        }
    }

    #endregion

    #region Interaction

    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        
        if (delta > 0)
        {
            // Zoom in
            _visibleBars = System.Math.Max(20, _visibleBars - 10);
        }
        else
        {
            // Zoom out
            _visibleBars = System.Math.Min(_bars.Count, _visibleBars + 10);
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
