namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Livery.Localization;
using Livery.Themes;
using SkiaSharp;
using ZettaTrade.Charting;
using ZettaTrade.UI.Resources;

/// <summary>
/// Equity-curve chart control for the trade-statistics window: draws the
/// cumulative realized-PnL area straight onto the Avalonia Skia canvas via
/// <see cref="ICustomDrawOperation"/> (the candlestick chart's draw canon —
/// no intermediate bitmaps). The curve is static data: the window's scan
/// publishes one immutable point sequence at a time and the control re-inks
/// per change, per theme and per language flip. Single points render as a
/// marker dot; an empty scope renders the localized no-data message — never
/// a fabricated flat line.
/// </summary>
public class EquityCurveChart : Control
{
    #region Constants

    /// <summary>Font size of the axis tick labels, like the candlestick chart.</summary>
    private const float AxisFontSizePx = 10f;

    /// <summary>Font size of the localized no-data message.</summary>
    private const float NoDataFontSizePx = 16f;

    /// <summary>Price tick slots between the padded extremes (six inclusive ticks).</summary>
    private const int PriceAxisLabelCount = 5;

    /// <summary>
    /// Horizontal padding between the plot and the value-label column,
    /// on both sides of the text (the candlestick canon).
    /// </summary>
    private const float PriceAxisGutterPadPx = 6f;

    /// <summary>Vertical padding of the bottom gutter around the time labels.</summary>
    private const float TimeAxisGutterPadPx = 6f;

    /// <summary>
    /// Horizontal spacing the measured time-label budget reserves between
    /// neighboring labels in addition to the widest label itself.
    /// </summary>
    private const float TimeLabelGapPx = 24f;

    /// <summary>
    /// Horizontal inset a centered time label keeps from the plot edges.
    /// </summary>
    private const float TimeLabelClampPadPx = 4f;

    /// <summary>
    /// Horizontal padding the time mapping keeps at both plot ends, as a
    /// ratio of the plot width: extreme points keep their marker/anchor
    /// inside the plot instead of clipping the stroke on the border.
    /// </summary>
    private const float TimePadRatio = 0.02f;

    /// <summary>Stroke width of the equity polyline.</summary>
    private const float CurveStrokeWidthPx = 2f;

    /// <summary>Area fill alpha over the curve's accent color.</summary>
    private const float AreaFillAlpha = 0.3f;

    /// <summary>Chart resource keys published by ThemeManager; the chart mirrors the live theme.</summary>
    private const string BackgroundColorKey = "Background";
    private const string GridColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string CurveColorKey = "IndicatorLine";

    /// <summary>The chart takes a screenshot of this resources key on each redraw.</summary>
    private const string NoDataMessageKey = "Chart.NoDataMessage";

    #endregion

    #region Styled Properties

    /// <summary>StyledProperty for the equity point sequence to render.</summary>
    public static readonly StyledProperty<IReadOnlyList<EquityCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<EquityCurveChart, IReadOnlyList<EquityCurvePoint>?>(nameof(Points));

    /// <summary>
    /// Gets or sets the chronological equity points the chart renders. A
    /// change is one fresh scan snapshot — the draw pass snapshots the
    /// sequence per render anyway, so an owned list is safe to hand over.
    /// </summary>
    public IReadOnlyList<EquityCurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    #endregion

    #region Fields & Properties

    // Theme-agnostic fallback colors: the built-in dark set, named so the
    // literals stay out of inline code; the live colors are refreshed from
    // the application resources on every snapshot (see RefreshThemeColors).
    private static readonly SKColor DefaultBackgroundColor = new(20, 20, 30);
    private static readonly SKColor DefaultGridColor = new(40, 40, 50);
    private static readonly SKColor DefaultTextColor = new(200, 200, 200);
    private static readonly SKColor DefaultCurveColor = new(245, 158, 11);

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private SKColor _gridColor = DefaultGridColor;
    private SKColor _textColor = DefaultTextColor;
    private SKColor _curveColor = DefaultCurveColor;

    private IDisposable? _themeChangeSubscription;
    private IDisposable? _languageChangeSubscription;

    #endregion

    #region Constructor

    static EquityCurveChart()
    {
        PointsProperty.Changed.AddClassHandler<EquityCurveChart>(
            (chart, _) => Dispatcher.UIThread.Post(chart.InvalidateVisual));
    }

    public EquityCurveChart()
    {
        ClipToBounds = true;
    }

    #endregion

    #region Theme & Language Tracking

    /// <summary>
    /// While attached, re-render on every theme or language change: the draw
    /// operation takes a snapshot per render, so invalidating is all it takes
    /// to pick up new colors and the localized 'no data' message.
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

    /// <summary>
    /// Refresh the draw colors from the theme resources published by ThemeManager
    /// (Application.Current.Resources); falls back to the dark-theme defaults.
    /// </summary>
    private void RefreshThemeColors()
    {
        _backgroundColor = ResourceColor(BackgroundColorKey, _backgroundColor);
        _gridColor = ResourceColor(GridColorKey, _gridColor);
        _textColor = ResourceColor(TextColorKey, _textColor);
        _curveColor = ResourceColor(CurveColorKey, _curveColor);
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

    #endregion Theme & Language Tracking

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
        // see an immutable snapshot, never the live (UI-thread) point list.
        context.Custom(new EquityCurveDrawOperation(new Rect(size), CreateSnapshot(), this));
    }

    /// <summary>
    /// Copies the point sequence and the resolved colors so the render thread
    /// works on its own data. Assumes <see cref="EquityCurvePoint"/> records
    /// are immutable once published.
    /// </summary>
    private EquityCurveSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        var points = Points;
        EquityCurvePoint[] visible = [];
        if (points is { Count: > 0 })
        {
            visible = [.. points];
        }

        return new EquityCurveSnapshot(
            visible,
            _backgroundColor,
            _gridColor,
            _textColor,
            _curveColor,
            ChartTickCulture.Resolve());
    }

    /// <summary>
    /// Immutable per-redraw state the render thread sees: the chronological
    /// equity points, the theme colors and the label culture resolved on the
    /// UI thread (the render thread reads no ambient CultureInfo — it belongs
    /// to the process default, not the app).
    /// </summary>
    private sealed record EquityCurveSnapshot(
        EquityCurvePoint[] Points,
        SKColor Background,
        SKColor Grid,
        SKColor Text,
        SKColor Curve,
        CultureInfo Culture);

    /// <summary>
    /// Draws the equity area straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class EquityCurveDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly EquityCurveSnapshot _snapshot;
        private readonly EquityCurveChart _owner;

        public EquityCurveDrawOperation(
            Rect bounds,
            EquityCurveSnapshot snapshot,
            EquityCurveChart owner)
        {
            _bounds = bounds;
            _snapshot = snapshot;
            _owner = owner;
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

            var points = _snapshot.Points;

            if (points.Length == 0)
            {
                _owner.DrawNoDataMessage(canvas, width, height);
                return;
            }

            // The vertical scale covers the curve AND the zero baseline, so a
            // one-sided curve keeps the honest zero line in view.
            var lowest = Math.Min(0m, points.Min(point => point.Value));
            var highest = Math.Max(0m, points.Max(point => point.Value));
            var (minValue, maxValue, valueRange) = ChartPriceScale.PaddedSpread(lowest, highest);

            using var font = new SKFont(SKTypeface.Default, AxisFontSizePx);
            using var gridPaint = new SKPaint
            {
                Color = _snapshot.Grid,
                StrokeWidth = 1,
                IsAntialias = true
            };
            using var textPaint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true
            };

            var valueTicks = ChartAxisFormatting.PickPriceTicks(minValue, maxValue, PriceAxisLabelCount);
            var valueDecimals = ChartAxisFormatting.PickPriceDecimals(valueRange / PriceAxisLabelCount);
            var valueTexts = valueTicks
                .Select(tick => ChartAxisFormatting.FormatPriceTick(tick, valueDecimals, _snapshot.Culture))
                .ToArray();
            var valueGutter = valueTexts.Max(text => font.MeasureText(text)) + (2f * PriceAxisGutterPadPx);

            // At least a one-pixel plot on an absurdly small window.
            var plotRect = new Rect(0, 0, Math.Max(1d, width - valueGutter), Math.Max(1d, height));

            DrawValueGrid(canvas, plotRect, valueTicks, minValue, valueRange, gridPaint);
            DrawCurve(canvas, plotRect, points, minValue, valueRange, points[0].Time, points[^1].Time);
            DrawValueAxis(canvas, font, textPaint, plotRect, valueTicks, valueTexts, minValue, valueRange);
            DrawTimeAxis(canvas, font, textPaint, plotRect, points[0].Time, points[^1].Time);
        }

        /// <summary>
        /// The zero-anchored value grid: horizontal lines at the tick ladder
        /// positions, mapped with the SAME scale geometry the curve uses.
        /// </summary>
        private static void DrawValueGrid(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<decimal> ticks,
            decimal minValue,
            decimal valueRange,
            SKPaint paint)
        {
            foreach (var tick in ticks)
            {
                var y = ChartPriceScale.PriceToY(plotRect, tick, minValue, valueRange);
                canvas.DrawLine((float)plotRect.Left, y, (float)plotRect.Right, y, paint);
            }
        }

        /// <summary>
        /// The equity area under the curve down to the zero baseline, then the
        /// stroke itself on top. Both ride the one time↔x and value↔y mapping,
        /// so the fill never lies about the shape it caps. A single point
        /// draws as a marker dot — there is no drawn curve to fabricate.
        /// </summary>
        private void DrawCurve(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<EquityCurvePoint> points,
            decimal minValue,
            decimal valueRange,
            DateTimeOffset firstTime,
            DateTimeOffset lastTime)
        {
            var baselineY = ChartPriceScale.PriceToY(plotRect, 0m, minValue, valueRange);

            using var fillPaint = new SKPaint
            {
                Color = _snapshot.Curve.WithAlpha((byte)(byte.MaxValue * AreaFillAlpha)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var strokePaint = new SKPaint
            {
                Color = _snapshot.Curve,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = CurveStrokeWidthPx,
                IsAntialias = true
            };

            if (points.Count == 1)
            {
                var onlyX = (float)TimeToX(plotRect, points[0].Time, firstTime, lastTime);
                var onlyY = ChartPriceScale.PriceToY(plotRect, points[0].Value, minValue, valueRange);
                canvas.DrawCircle(onlyX, onlyY, CurveStrokeWidthPx, strokePaint);
                return;
            }

            using var builder = new SKPathBuilder();

            builder.MoveTo((float)plotRect.Left, baselineY);
            foreach (var point in points)
            {
                builder.LineTo(
                    (float)TimeToX(plotRect, point.Time, firstTime, lastTime),
                    ChartPriceScale.PriceToY(plotRect, point.Value, minValue, valueRange));
            }

            builder.LineTo((float)plotRect.Right, baselineY);

            using var drawnPath = builder.Detach();
            canvas.DrawPath(drawnPath, fillPaint);

            // The stroke rides the plotted polyline only (not the area's
            // baseline legs), so the shape reads as a curve, not a bordered box.
            using var strokeBuilder = new SKPathBuilder();
            var strokeStarted = false;
            foreach (var point in points)
            {
                var x = (float)TimeToX(plotRect, point.Time, firstTime, lastTime);
                var y = ChartPriceScale.PriceToY(plotRect, point.Value, minValue, valueRange);
                if (!strokeStarted)
                {
                    strokeBuilder.MoveTo(x, y);
                    strokeStarted = true;
                }
                else
                {
                    strokeBuilder.LineTo(x, y);
                }
            }


            using var strokedPath = strokeBuilder.Detach();
            canvas.DrawPath(strokedPath, strokePaint);
        }

        /// <summary>
        /// Value labels in the right gutter, LEFT-aligned as one column
        /// starting just past the plot edge and vertically CENTERED on their
        /// gridline (the candlestick canon).
        /// </summary>
        private static void DrawValueAxis(
            SKCanvas canvas,
            SKFont font,
            SKPaint paint,
            Rect plotRect,
            IReadOnlyList<decimal> ticks,
            IReadOnlyList<string> texts,
            decimal minValue,
            decimal valueRange)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                var y = ChartPriceScale.PriceToY(plotRect, ticks[i], minValue, valueRange);
                var centerY = y - ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);

                canvas.DrawText(texts[i], (float)plotRect.Right + PriceAxisGutterPadPx, centerY, SKTextAlign.Left, font, paint);
            }
        }

        /// <summary>
        /// Time labels centered under their ticks inside the bottom gutter,
        /// clamped inside the plot horizontally (the candlestick canon). The
        /// ticks ride the same proportionally-padded time mapping the curve
        /// uses, so every label centers on the moment it names.
        /// </summary>
        private void DrawTimeAxis(
            SKCanvas canvas,
            SKFont font,
            SKPaint paint,
            Rect plotRect,
            DateTimeOffset firstTime,
            DateTimeOffset lastTime)
        {
            var (ticks, texts) = PickTimeLabels(
                firstTime,
                lastTime,
                plotRect.Width,
                font,
                _snapshot.Culture);

            foreach (var (tick, text) in ticks.Zip(texts))
            {
                var centered = (float)TimeToX(plotRect, tick, firstTime, lastTime);
                var halfWidth = font.MeasureText(text) / 2f;
                var minCenter = halfWidth + TimeLabelClampPadPx;
                var maxCenter = (float)plotRect.Right - halfWidth - TimeLabelClampPadPx;

                // When the plot is narrower than one label the two clamp
                // bounds collapse — Max(min, max) then keeps the left bound,
                // like the candlestick canon: the degenerate case clips a
                // label, it never throws.
                var x = Math.Min(Math.Max(centered, minCenter), Math.Max(minCenter, maxCenter));
                var baseline = (float)plotRect.Bottom + TimeAxisGutterPadPx - font.Metrics.Ascent;

                canvas.DrawText(text, x, baseline, SKTextAlign.Center, font, paint);
            }
        }

        /// <summary>
        /// Two-pass time-label pick, the candlestick canon: the default
        /// budget anchors the first estimate; the widest label it produced
        /// then measures the REAL budget for this plot width, and a differing
        /// budget re-picks onto the rung whose labels stay within the
        /// measured width, so one measurement pass suffices.
        /// </summary>
        private static (IReadOnlyList<DateTimeOffset> Ticks, string[] Texts) PickTimeLabels(
            DateTimeOffset firstTime,
            DateTimeOffset lastTime,
            double plotWidth,
            SKFont font,
            CultureInfo culture)
        {
            var timeSpan = lastTime - firstTime;
            var rung = ChartAxisFormatting.PickTimeStep(timeSpan, ChartAxisFormatting.DefaultTimeTickBudget);
            var ticks = ChartAxisFormatting.PickTimeTicks(firstTime, lastTime, rung, ChartAxisFormatting.DefaultTimeTickBudget);
            string[] texts = [.. ticks.Select(tick => ChartAxisFormatting.FormatTimeTick(tick, rung, timeSpan, culture, use12HourClock: false))];
            var widest = WidestLabelPx(texts, font);
            var budget = ChartAxisFormatting.TimeTickBudgetFor(plotWidth, widest, TimeLabelGapPx);

            if (budget == ChartAxisFormatting.DefaultTimeTickBudget)
            {
                return (ticks, texts);
            }

            rung = ChartAxisFormatting.PickTimeStep(timeSpan, budget);
            ticks = ChartAxisFormatting.PickTimeTicks(firstTime, lastTime, rung, budget);

            return (ticks, [.. ticks.Select(tick => ChartAxisFormatting.FormatTimeTick(tick, rung, timeSpan, culture, use12HourClock: false))]);
        }

        private static double WidestLabelPx(IReadOnlyList<string> texts, SKFont font) =>
            texts.Count == 0 ? 0 : texts.Max(text => font.MeasureText(text));

        /// <summary>
        /// The proportionally-padded time mapping: the zero-value "curve
        /// headroom" of the time axis. Equal timestamps collapse the width
        /// to the plot's center — a degenerate case maps honestly instead of
        /// dividing by zero.
        /// </summary>
        private static double TimeToX(
            Rect plotRect,
            DateTimeOffset time,
            DateTimeOffset firstTime,
            DateTimeOffset lastTime)
        {
            if (firstTime == lastTime)
            {
                return plotRect.Width / 2;
            }

            var pad = plotRect.Width * TimePadRatio;
            var usable = Math.Max(1d, plotRect.Width - (2 * pad));
            var fraction = (time - firstTime) / (lastTime - firstTime);

            return pad + (usable * (double)fraction);
        }
    }

    private void DrawNoDataMessage(SKCanvas canvas, float width, float height)
    {
        using var paint = new SKPaint
        {
            Color = _textColor,
            IsAntialias = true
        };

        using var font = new SKFont(SKTypeface.Default, NoDataFontSizePx);

        var text = GetLocalizedNoDataText();
        canvas.DrawText(
            text,
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

    #endregion
}
