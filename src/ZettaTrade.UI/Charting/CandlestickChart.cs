namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
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
using Livery.Localization;
using Livery.Resources;
using Livery.Themes;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Candlestick chart control that draws directly onto the Avalonia Skia canvas
/// via <see cref="ICustomDrawOperation"/> (no intermediate bitmaps or pixel copies).
/// The pointer hover is part of the same pipeline: <see cref="OnPointerMoved"/>
/// tracks the cursor, the draw pass renders the dashed crosshair rails, the
/// hovered price pill in the right gutter and the hovered candle's OHLC
/// readout off an immutable snapshot copy.
/// </summary>
public class CandlestickChart : Control
{
    #region Constants

    private const int MinVisibleBars = 20;
    private const int DefaultVisibleBars = 100;
    private const int VisibleBarsZoomStep = 10;

    /// <summary>Price tick slots between the padded extremes (six inclusive ticks).</summary>
    private const int PriceAxisLabelCount = 5;
    private const float CandleBodyWidthRatio = 0.7f;
    private const float MinCandleBodyHeight = 1f;
    private const float NoDataFontSize = 16f;
    private const float AxisFontSize = 10f;

    /// <summary>
    /// Horizontal padding between the plot and the price-label column, on
    /// both sides of the text: the right gutter is sized to the widest
    /// measured label, so no price label can cross the canvas edge.
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
    /// Dash pattern (on/off pixels) of the hover crosshair rails.
    /// </summary>
    private static readonly float[] CrosshairDashPatternPx = [4f, 4f];

    /// <summary>
    /// Padding around the hovered-candle OHLC readout inside the plot's
    /// top-left corner.
    /// </summary>
    private const float CrosshairReadoutPadPx = 8f;

    /// <summary>
    /// Padding between the hover price-pill text and its background edges.
    /// </summary>
    private const float CrosshairPillPadPx = 3f;

    /// <summary>
    /// Upper bound of the bar pitch (the full slot width one candle
    /// occupies). When fewer bars are loaded than the visible window wants,
    /// they keep a terminal-normal candle width and anchor to the plot's
    /// right edge (the empty space grows left) instead of stretching across
    /// the whole plot.
    /// </summary>
    private const float MaxBarPitchPx = 40f;

    /// <summary>
    /// Horizontal inset a centered time label keeps from the plot edges when
    /// its tick sits at (or near) a boundary.
    /// </summary>
    private const float TimeLabelClampPadPx = 4f;

    /// <summary>The chart takes a screenshot of these resources on each redraw.</summary>
    private const string NoDataMessageKey = "Chart.NoDataMessage";

    /// <summary>Resource keys published by ThemeManager; the chart mirrors the current theme live.</summary>
    private const string BackgroundColorKey = "Background";
    private const string CandleUpColorKey = "CandleUp";
    private const string CandleDownColorKey = "CandleDown";
    private const string GridColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string IndicatorLineColorKey = "IndicatorLine";

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

    /// <summary>
    /// StyledProperty for the half-day clock of the time axis.
    /// </summary>
    public static readonly StyledProperty<bool> Use12HourClockProperty =
        AvaloniaProperty.Register<CandlestickChart, bool>(nameof(Use12HourClock));

    /// <summary>
    /// StyledProperty for the indicator overlay lines: one
    /// <see cref="ChartIndicatorLine"/> per enabled settings row, its values
    /// aligned with <see cref="Bars"/> by index (the ViewModel computes them;
    /// the draw pass slices with the visible window). Empty - the default -
    /// draws no overlay.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<ChartIndicatorLine>> IndicatorLinesProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<ChartIndicatorLine>>(
            nameof(IndicatorLines),
            defaultValue: []);

    /// <summary>
    /// Whether the time axis prints the 12-hour clock ("11:45 PM"); false вЂ”
    /// the default вЂ” prints the 24-hour "23:45". The chart owns no
    /// persistence: the settings service is the source of truth, rendering
    /// flows the applied value in through this StyledProperty binding.
    /// </summary>
    public bool Use12HourClock
    {
        get => GetValue(Use12HourClockProperty);
        set => SetValue(Use12HourClockProperty, value);
    }

    /// <summary>The indicator overlay lines drawn over the candles; a change re-inks on the next pass.</summary>
    public IReadOnlyList<ChartIndicatorLine> IndicatorLines
    {
        get => GetValue(IndicatorLinesProperty);
        set => SetValue(IndicatorLinesProperty, value);
    }

    #endregion

    #region Fields & Properties

    private int _visibleBars = DefaultVisibleBars;

    /// <summary>
    /// The cursor's position in control coordinates, or null when the
    /// pointer has never entered the control or has left it. UI-thread
    /// state only: <see cref="CreateSnapshot"/> copies it into the snapshot
    /// the render thread draws from.
    /// </summary>
    private Point? _hoverPosition;

    // Theme-agnostic fallback colors: the built-in dark set, named so the
    // literals stay out of inline code; the live colors are refreshed from
    // the application resources on every snapshot (see RefreshThemeColors).
    private static readonly SKColor DefaultBackgroundColor = new(20, 20, 30);
    private static readonly SKColor DefaultBullishColor = new(34, 197, 94);
    private static readonly SKColor DefaultBearishColor = new(239, 68, 68);
    private static readonly SKColor DefaultGridColor = new(40, 40, 50);
    private static readonly SKColor DefaultTextColor = new(200, 200, 200);
    private static readonly SKColor DefaultIndicatorLineColor = new(245, 158, 11);

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private SKColor _bullishColor = DefaultBullishColor;
    private SKColor _bearishColor = DefaultBearishColor;
    private SKColor _gridColor = DefaultGridColor;
    private SKColor _textColor = DefaultTextColor;
    private SKColor _indicatorLineColor = DefaultIndicatorLineColor;

    private IDisposable? _themeChangeSubscription;
    private IDisposable? _languageChangeSubscription;

    /// <summary>
    /// The pan offset in bar slots: the count of loaded bars hidden at the
    /// series' RIGHT end (zero keeps the newest bar at the plot's right edge
    /// — the standing behavior). Zero on every new Bars instance (a rebase),
    /// untouched by in-place appends and prepends: prepends only add older
    /// bars to the series' left, so the offset still names the same bars.
    /// UI-thread state only: pointer events mutate it, <see cref="CreateSnapshot"/>
    /// copies its effect into the snapshot the render thread draws from.
    /// </summary>
    private int _panOffset;

    /// <summary>
    /// The control-space X where the active pan drag started, or null when
    /// no drag is active (no pointer captured / released). UI-thread state only.
    /// </summary>
    private double? _panAnchorX;

    /// <summary>
    /// The older-history requests stream sink (hot; see <see cref="OlderHistoryRequested"/>).
    /// </summary>
    private readonly Subject<ChartOlderHistoryRequestedEventArgs> _olderHistoryRequested = new();

    /// <summary>
    /// The older-history requests stream, hot and fed on the UI thread: a
    /// pan gesture that moves the window while its left edge stands on the
    /// loaded series' first bar (see <see cref="ChartPanWindow.OlderFetchThresholdReached"/>).
    /// The ViewModel answers with a fetch of the previous windows.
    /// </summary>
    public IObservable<ChartOlderHistoryRequestedEventArgs> OlderHistoryRequested => _olderHistoryRequested;

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

        // A clock-format flip re-inks the axis labels on the next pass.
        Use12HourClockProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        // A new overlay set re-inks on the next pass: the draw operation
        // snapshots the property per render anyway.
        IndicatorLinesProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));
    }

    public CandlestickChart()
    {
        Focusable = true;
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
        _bullishColor = ResourceColor(CandleUpColorKey, _bullishColor);
        _bearishColor = ResourceColor(CandleDownColorKey, _bearishColor);
        _gridColor = ResourceColor(GridColorKey, _gridColor);
        _textColor = ResourceColor(TextColorKey, _textColor);
        _indicatorLineColor = ResourceColor(IndicatorLineColorKey, _indicatorLineColor);
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

        // A new Bars instance is a rebase (a full reload): the pan offset is
        // defined against the loaded series, so the window returns to the
        // newest bar. In-place edits keep it — prepends add older bars to
        // the left only, so the offset keeps pointing at the same bars.
        _panOffset = 0;

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
        RefreshThemeColors();

        Bar[] visible = [];
        var bars = Bars;
        var spanStart = 0;
        var spanEnd = 0;

        if (bars is { Count: > 0 })
        {
            (spanStart, spanEnd) = ChartPanWindow.VisibleSpan(bars.Count, _panOffset, _visibleBars);

            visible = new Bar[spanEnd - spanStart];
            for (var i = 0; i < visible.Length; i++)
            {
                visible[i] = bars[spanStart + i];
            }
        }

        return new ChartSnapshot(
            visible,
            SliceIndicatorLines(bars, spanStart, spanEnd),
            _backgroundColor,
            _bullishColor,
            _bearishColor,
            _gridColor,
            _textColor,
            _indicatorLineColor,
            ChartTickCulture.Resolve(),
            Use12HourClock,
            _hoverPosition);
    }

    /// <summary>
    /// Slice every overlay line with the SAME [start, end) window the bars
    /// slice uses. A line whose values no longer align with the current
    /// series (a stale publish racing the new Bars instance) is skipped
    /// wholesale - a length mismatch can never reach the draw pass as a
    /// silently shifted polyline.
    /// </summary>
    private IReadOnlyList<ChartIndicatorLine> SliceIndicatorLines(
        ObservableCollection<Bar>? bars,
        int spanStart,
        int spanEnd)
    {
        if (bars is not { Count: > 0 })
        {
            return [];
        }

        var current = IndicatorLines;
        if (current.Count == 0)
        {
            return [];
        }

        List<ChartIndicatorLine> slices = [];
        foreach (var line in current)
        {
            if (line.Values.Count != bars.Count)
            {
                continue;
            }

            var slice = new decimal?[spanEnd - spanStart];
            for (var i = 0; i < slice.Length; i++)
            {
                slice[i] = line.Values[spanStart + i];
            }

            slices.Add(new ChartIndicatorLine(line.Name, slice));
        }

        return slices;
    }

    /// <summary>
    /// Immutable per-redraw state the render thread sees: the visible bars,
    /// the theme colors, the label culture + clock format resolved on the
    /// UI thread (the render thread reads no live chart state вЂ” an ambient
    /// CultureInfo there belongs to the process default, not the app), and
    /// the hover position captured from the pointer events on the UI thread
    /// (null when the cursor is outside the control or has left it).
    /// </summary>
    private sealed record ChartSnapshot(
        Bar[] VisibleBars,
        IReadOnlyList<ChartIndicatorLine> Indicators,
        SKColor Background,
        SKColor Bullish,
        SKColor Bearish,
        SKColor Grid,
        SKColor Text,
        SKColor IndicatorLine,
        CultureInfo Culture,
        bool Use12HourClock,
        Point? Hover);

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

            var lowest = visibleBars.Min(b => b.Low);
            var highest = visibleBars.Max(b => b.High);
            var (minPrice, maxPrice, priceRange) = ChartPriceScale.PaddedSpread(lowest, highest);

            // The labels decide the geometry FIRST: gutters size themselves
            // to the measured texts so no label can clip, and the plot
            // shrinks to leave room for them. Zoom re-buckets the axes free
            // of charge: the wheel narrows or widens the visible window, the
            // window changes the price spread AND the time span, and both
            // label sets follow.
            using var font = new SKFont(SKTypeface.Default, AxisFontSize);
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

            var priceTicks = ChartAxisFormatting.PickPriceTicks(minPrice, maxPrice, PriceAxisLabelCount);
            var priceDecimals = ChartAxisFormatting.PickPriceDecimals(priceRange / PriceAxisLabelCount);
            var priceTexts = priceTicks
                .Select(tick => ChartAxisFormatting.FormatPriceTick(tick, priceDecimals, _snapshot.Culture))
                .ToArray();
            var priceGutter = priceTexts.Max(text => font.MeasureText(text)) + (2f * PriceAxisGutterPadPx);

            var openTimes = ChartBarLayout.CollectOpenTimes(visibleBars);
            var firstTime = openTimes[0];
            var lastTime = openTimes[^1];

            // The time-label budget calibrates against the plot width the
            // labels will actually span (the width minus the measured price
            // gutter), so it is cut before the plot rect exists.
            var plotWidth = Math.Max(1d, width - priceGutter);
            var (timeRung, timeTicks, timeTexts) = PickTimeLabels(
                firstTime,
                lastTime,
                plotWidth,
                font,
                _snapshot.Culture,
                _snapshot.Use12HourClock);

            var textHeight = -font.Metrics.Ascent + font.Metrics.Descent;
            var timeGutter = textHeight + (2f * TimeAxisGutterPadPx);

            // At least a one-pixel plot on an absurdly small window.
            var plotRect = new Rect(0, 0, plotWidth, Math.Max(1d, height - timeGutter));

            // The bar pitch is window-driven and capped: when fewer bars are
            // loaded than the window wants, they keep a normal candle width
            // and anchor to the plot's right edge (empty space grows left)
            // instead of stretching into a full-width wall. Grid lines and
            // label centers ride the SAME slots as the candles.
            var barPitch = ChartBarLayout.BarPitch(plotRect.Width, visibleBars.Length, MaxBarPitchPx);

            DrawGrid(
                canvas,
                gridPaint,
                plotRect,
                minPrice,
                priceRange,
                priceTicks,
                timeTicks,
                barPitch,
                openTimes);

            DrawCandles(canvas, plotRect, visibleBars, barPitch, minPrice, priceRange);
            DrawIndicatorLines(canvas, plotRect, visibleBars, barPitch, minPrice, priceRange, _snapshot.Indicators);
            DrawPriceAxis(canvas, font, textPaint, plotRect, minPrice, priceRange, priceTicks, priceTexts);
            DrawTimeAxis(canvas, font, textPaint, plotRect, barPitch, openTimes, timeTicks, timeTexts);

            // The hover layer rides LAST: the crosshair paints over the
            // candles and the axis labels (the terminal look).
            DrawHover(
                canvas,
                font,
                textPaint,
                plotRect,
                visibleBars,
                barPitch,
                minPrice,
                priceRange,
                priceDecimals,
                _snapshot.Hover);
        }

        private void DrawNoDataMessage(SKCanvas canvas, double width, double height)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true
            };

            using var font = new SKFont(SKTypeface.Default, NoDataFontSize);

            var text = GetLocalizedNoDataText();
            canvas.DrawText(
                text,
                (float)width / 2,
                (float)height / 2,
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

        /// <summary>
        /// Grid lines AT the tick positions: horizontal lines ride the price
        /// ladder (aligned with the price labels), vertical lines ride the
        /// time ticks вЂ” mapped onto the SAME bar slots the candles occupy.
        /// Both stay strictly inside the plot, so the gutters and the canvas
        /// edges stay clean.
        /// </summary>
        private static void DrawGrid(
            SKCanvas canvas,
            SKPaint paint,
            Rect plotRect,
            decimal minPrice,
            decimal priceRange,
            IReadOnlyList<decimal> priceTicks,
            IReadOnlyList<DateTimeOffset> timeTicks,
            double barPitch,
            IReadOnlyList<DateTimeOffset> openTimes)
        {
            foreach (var tick in priceTicks)
            {
                var y = ChartPriceScale.PriceToY(plotRect, tick, minPrice, priceRange);
                canvas.DrawLine((float)plotRect.Left, y, (float)plotRect.Right, y, paint);
            }

            foreach (var tick in timeTicks)
            {
                var x = (float)ChartBarLayout.BarXForTime(plotRect.Right, barPitch, openTimes, tick);
                canvas.DrawLine(x, (float)plotRect.Top, x, (float)plotRect.Bottom, paint);
            }
        }

        private void DrawCandles(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minPrice,
            decimal priceRange)
        {
            var bodyWidth = (float)(barPitch * CandleBodyWidthRatio);

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
                var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, bars.Count, i);

                var isBullish = bar.Close >= bar.Open;
                var paint = isBullish ? bullishPaint : bearishPaint;

                var openY = ChartPriceScale.PriceToY(plotRect, bar.Open, minPrice, priceRange);
                var closeY = ChartPriceScale.PriceToY(plotRect, bar.Close, minPrice, priceRange);
                var highY = ChartPriceScale.PriceToY(plotRect, bar.High, minPrice, priceRange);
                var lowY = ChartPriceScale.PriceToY(plotRect, bar.Low, minPrice, priceRange);

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

        /// <summary>
        /// The indicator overlay polylines drawn straight over the candles (before
        /// the axis layers and the hover): every line rides the same bar slots and
        /// the same price scale as the candles - never its own axis. A warmup null
        /// breaks the polyline; the segments stay inside the plot rect (the canvas
        /// clip exists, the own mapping guarantee is not needed).
        /// </summary>
        private void DrawIndicatorLines(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minPrice,
            decimal priceRange,
            IReadOnlyList<ChartIndicatorLine> indicators)
        {
            if (indicators.Count == 0)
            {
                return;
            }

            using var paint = new SKPaint
            {
                Color = _snapshot.IndicatorLine,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };

            foreach (var indicator in indicators)
            {
                var values = indicator.Values;
                if (values.Count != bars.Count)
                {
                    continue;
                }

                using var path = new SKPathBuilder();
                var penDown = false;
                for (var i = 0; i < bars.Count; i++)
                {
                    if (values[i] is not { } value)
                    {
                        penDown = false;
                        continue;
                    }

                    var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, bars.Count, i);
                    var y = ChartPriceScale.PriceToY(plotRect, value, minPrice, priceRange);

                    if (!penDown)
                    {
                        path.MoveTo(x, y);
                        penDown = true;
                    }
                    else
                    {
                        path.LineTo(x, y);
                    }
                }

                canvas.DrawPath(path.Detach(), paint);
            }
        }

        /// <summary>
        /// Price labels in the right gutter, LEFT-aligned as one column
        /// starting just past the plot edge and vertically CENTERED on their
        /// gridline (metrics-based). The old placement drew with an
        /// edge-relative baseline, clipping the extreme labels off the canvas.
        /// </summary>
        private static void DrawPriceAxis(
            SKCanvas canvas,
            SKFont font,
            SKPaint paint,
            Rect plotRect,
            decimal minPrice,
            decimal priceRange,
            IReadOnlyList<decimal> ticks,
            IReadOnlyList<string> texts)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                var y = ChartPriceScale.PriceToY(plotRect, ticks[i], minPrice, priceRange);
                var centerY = y - ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);

                canvas.DrawText(texts[i], (float)plotRect.Right + PriceAxisGutterPadPx, centerY, SKTextAlign.Left, font, paint);
            }
        }

        /// <summary>
        /// Two-pass time-label pick. The default budget anchors the first
        /// estimate; the widest label it produced then measures the REAL
        /// budget for this plot width, and a differing budget re-picks.
        /// Re-picking moves finer for a widened budget (shorter labels, so
        /// the measurement stays a valid upper bound) or coarser onto
        /// calendar scales whose labels stay within the measured width, so
        /// one measurement pass suffices. The rung, ticks and texts always
        /// belong to the same final pick. The culture and the clock format
        /// ride the snapshot (the render thread reads no ambient state) вЂ”
        /// every redraw resolves them from the applied UI language.
        /// </summary>
        private static (TimeTickRung Rung, IReadOnlyList<DateTimeOffset> Ticks, string[] Texts) PickTimeLabels(
            DateTimeOffset firstTime,
            DateTimeOffset lastTime,
            double plotWidth,
            SKFont font,
            CultureInfo culture,
            bool use12HourClock)
        {
            var timeSpan = lastTime - firstTime;
            var rung = ChartAxisFormatting.PickTimeStep(timeSpan, ChartAxisFormatting.DefaultTimeTickBudget);
            var ticks = ChartAxisFormatting.PickTimeTicks(firstTime, lastTime, rung, ChartAxisFormatting.DefaultTimeTickBudget);
            var texts = FormatTimeTexts(ticks, rung, timeSpan, culture, use12HourClock);
            var widest = WidestLabelPx(texts, font);
            var budget = ChartAxisFormatting.TimeTickBudgetFor(plotWidth, widest, TimeLabelGapPx);

            if (budget == ChartAxisFormatting.DefaultTimeTickBudget)
            {
                return (rung, ticks, texts);
            }

            rung = ChartAxisFormatting.PickTimeStep(timeSpan, budget);
            ticks = ChartAxisFormatting.PickTimeTicks(firstTime, lastTime, rung, budget);

            return (rung, ticks, FormatTimeTexts(ticks, rung, timeSpan, culture, use12HourClock));
        }

        private static string[] FormatTimeTexts(
            IReadOnlyList<DateTimeOffset> ticks,
            TimeTickRung rung,
            TimeSpan timeSpan,
            IFormatProvider culture,
            bool use12HourClock) =>
            [.. ticks.Select(tick => ChartAxisFormatting.FormatTimeTick(tick, rung, timeSpan, culture, use12HourClock))];

        private static double WidestLabelPx(IReadOnlyList<string> texts, SKFont font) =>
            texts.Count == 0 ? 0 : texts.Max(text => font.MeasureText(text));

        /// <summary>
        /// Time labels centered under their ticks inside the bottom gutter
        /// (metrics-based baseline), with the center-x clamped INSIDE the
        /// plot horizontally: the old placement centered the first label on
        /// the exact canvas edge and half of the text fell off. The label
        /// pick already formatted the texts in tick order.
        /// </summary>
        private static void DrawTimeAxis(
            SKCanvas canvas,
            SKFont font,
            SKPaint paint,
            Rect plotRect,
            double barPitch,
            IReadOnlyList<DateTimeOffset> openTimes,
            IReadOnlyList<DateTimeOffset> ticks,
            IReadOnlyList<string> texts)
        {
            for (var i = 0; i < ticks.Count; i++)
            {
                var text = texts[i];
                var halfWidth = font.MeasureText(text) / 2f;
                var centered = (float)ChartBarLayout.BarXForTime(plotRect.Right, barPitch, openTimes, ticks[i]);
                var minCenter = halfWidth + TimeLabelClampPadPx;
                var maxCenter = (float)plotRect.Right - halfWidth - TimeLabelClampPadPx;

                // When the plot is narrower than one label the two clamp
                // bounds collapse вЂ” Max(min, max) then keeps the left bound:
                // the degenerate case clips a label, it never throws.
                var x = Math.Min(Math.Max(centered, minCenter), Math.Max(minCenter, maxCenter));
                var baseline = (float)plotRect.Bottom + TimeAxisGutterPadPx - font.Metrics.Ascent;

                canvas.DrawText(text, x, baseline, SKTextAlign.Center, font, paint);
            }
        }

        /// <summary>
        /// The hover layer, drawn last: dashed crosshair rails clipped to the
        /// plot, the hovered price pill in the right gutter at the cursor's
        /// row, and the hovered candle's compact OHLC (+V) readout in the
        /// plot's top-left. Everything keys off the hover position the
        /// snapshot carries: no hover, a cursor outside the plot band (a
        /// rail just does not draw) or a cursor outside every bar's
        /// half-pitch band (no readout) draw less of it instead of throwing
        /// or substituting вЂ” the dead paths stay silent. The cursor's price
        /// maps back onto the padded scale through the SAME
        /// <see cref="ChartPriceScale"/> geometry the candles and axis
        /// labels use, so the pill and the readout always agree with the
        /// pixels under the cursor.
        /// </summary>
        private void DrawHover(
            SKCanvas canvas,
            SKFont font,
            SKPaint textPaint,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minPrice,
            decimal priceRange,
            int priceDecimals,
            Point? hover)
        {
            if (hover is not { } position)
            {
                return;
            }

            var x = (float)position.X;
            var y = (float)position.Y;
            var textHeight = -font.Metrics.Ascent + font.Metrics.Descent;

            // The rails: 1 px dashed lines riding the plot's inside only вЂ”
            // a cursor in a gutter shows no rail on that axis. SkiaSharp
            // refcounts the native dash effect: the paint holds one
            // reference, this call holds the other, both die here.
            using var dashEffect = SKPathEffect.CreateDash(CrosshairDashPatternPx, 0f);
            using var railPaint = new SKPaint
            {
                Color = _snapshot.Grid,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true,
                PathEffect = dashEffect
            };

            if ((x >= plotRect.Left) && (x <= plotRect.Right))
            {
                canvas.DrawLine(x, (float)plotRect.Top, x, (float)plotRect.Bottom, railPaint);
            }

            if ((y >= plotRect.Top) && (y <= plotRect.Bottom))
            {
                canvas.DrawLine((float)plotRect.Left, y, (float)plotRect.Right, y, railPaint);
            }

            // The hovered price pill: the cursor's y mapped back onto the
            // padded scale, printed in the right gutter at the cursor's row
            // (backgrounded, so it stays readable over the tick labels).
            var pillText = ChartAxisFormatting.FormatPriceTick(
                ChartPriceScale.YToPrice(position.Y, plotRect, minPrice, priceRange),
                priceDecimals,
                _snapshot.Culture);
            var pillWidth = font.MeasureText(pillText) + (2f * CrosshairPillPadPx);
            var pillHeight = textHeight + (2f * CrosshairPillPadPx);
            var pillBaseline = y - ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);

            using var pillPaint = new SKPaint
            {
                Color = _snapshot.Grid,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            canvas.DrawRect((float)plotRect.Right, y - (pillHeight / 2f), pillWidth, pillHeight, pillPaint);
            canvas.DrawText(pillText, (float)plotRect.Right + CrosshairPillPadPx, pillBaseline, SKTextAlign.Left, font, textPaint);

            // The hovered candle's compact readout in the plot's top-left:
            // O/H/L/C plus volume of the bar under the cursor x. The numbers
            // reuse the price-axis formatting (same decimals, same culture вЂ”
            // numbers need no resx), the letter glyphs are the universal
            // trading notation, unlocalized per the board's scope.
            var barIndex = ChartBarLayout.SlotIndexForX(plotRect.Right, barPitch, bars.Count, position.X);

            if (barIndex < 0)
            {
                return;
            }

            var bar = bars[barIndex];
            var openText = ChartAxisFormatting.FormatPriceTick(bar.Open, priceDecimals, _snapshot.Culture);
            var highText = ChartAxisFormatting.FormatPriceTick(bar.High, priceDecimals, _snapshot.Culture);
            var lowText = ChartAxisFormatting.FormatPriceTick(bar.Low, priceDecimals, _snapshot.Culture);
            var closeText = ChartAxisFormatting.FormatPriceTick(bar.Close, priceDecimals, _snapshot.Culture);
            var volumeText = ChartAxisFormatting.FormatPriceTick(bar.Volume, priceDecimals, _snapshot.Culture);
            var readout = $"O {openText}  H {highText}  L {lowText}  C {closeText}  V {volumeText}";
            var readoutBaseline = (float)plotRect.Top + CrosshairReadoutPadPx - font.Metrics.Ascent;

            canvas.DrawText(readout, (float)plotRect.Left + CrosshairReadoutPadPx, readoutBaseline, SKTextAlign.Left, font, textPaint);
        }
    }

    #endregion

    #region Interaction

    /// <summary>
    /// Track the cursor for the hover layer: every move re-snapshots the
    /// position (the chart redraws from the new snapshot), a same-position
    /// move event changes nothing and skips the invalidation.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        // An armed pan drag scrolls first (whole bar slots); the hover
        // crosshair rides the same pointer either way.
        ApplyPan(position.X);

        if (_hoverPosition is { } last && last == position)
        {
            return;
        }

        _hoverPosition = position;
        InvalidateVisual();
    }

    /// <summary>
    /// The pointer left the control: the hover layer has nothing to draw on.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (_hoverPosition is null)
        {
            return;
        }

        _hoverPosition = null;
        InvalidateVisual();
    }

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

    /// <summary>
    /// A left-button press arms a pan drag and captures the pointer: the
    /// press itself pans nothing, so the chart is never dragged by an
    /// accidental click (and the click keeps its <see cref="Focus"/> job).
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _panAnchorX = e.GetPosition(this).X;
            e.Pointer.Capture(this);
        }

        Focus();
    }

    /// <summary>
    /// The pan gesture ended: the anchor drops, the offset stands.
    /// </summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        EndPan();
    }

    /// <summary>
    /// The capture was lost mid-drag (the pointer left the window, a
    /// menu grabbed it): end the pan rather than leaving a ghost anchor.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        EndPan();
    }

    /// <summary>
    /// Ends the pan drag: the anchor drops, the offset stands.
    /// </summary>
    private void EndPan()
    {
        _panAnchorX = null;
    }

    /// <summary>
    /// One moved-slot step of the pan: converts the pixel distance dragged,
    /// relative to the anchor, into whole bar slots (the pan scrolls in
    /// integer slots — the pitch is the same window-driven one the draw
    /// pass paints with, so the scroll distance matches the bar width).
    /// </summary>
    private void ApplyPan(double draggedX)
    {
        var bars = Bars;
        if (_panAnchorX is not { } anchorX || bars is not { Count: > 0 })
        {
            return;
        }

        var barCount = bars.Count;

        // The pan step paces the slots the draw pass paints per pixel: the
        // pitch comes from the VISIBLE SPAN (the draw pass computes it from
        // the same slice), not from the raw window size - a shrunken span
        // (a starved series, or a start-clamped window) would otherwise
        // scroll at a different speed than the candles.
        var (spanStart, spanEnd) = ChartPanWindow.VisibleSpan(barCount, _panOffset, _visibleBars);
        var pitch = ChartBarLayout.BarPitch(Bounds.Width, spanEnd - spanStart, MaxBarPitchPx);
        if (pitch <= 0)
        {
            return;
        }

        // Dragging RIGHT slides the timeline right: older bars enter the
        // plot from the left, the offset grows. The distance is taken from
        // the drag's own anchor (not per-event deltas) so a wave of move
        // events accumulates without rounding drift.
        var slots = (int)Math.Round((draggedX - anchorX) / pitch);
        var moved = ChartPanWindow.ClampOffset(barCount, _panOffset + slots);
        if (moved == _panOffset)
        {
            return;
        }

        _panOffset = moved;
        InvalidateVisual();

        // The window moved while its left edge stands on the series' first
        // bar: older history is needed to keep panning. One request per
        // moved slot — the ViewModel's interlock serializes the pages.
        if (!ChartPanWindow.OlderFetchThresholdReached(barCount, moved, _visibleBars))
        {
            return;
        }

        var (start, _) = ChartPanWindow.VisibleSpan(barCount, moved, _visibleBars);
        _olderHistoryRequested.OnNext(new ChartOlderHistoryRequestedEventArgs(bars[start].Time));
    }

    #endregion

    /// <summary>
    /// One older-history request from a pan gesture: the oldest bar time
    /// the visible window reached at the threshold moment. Context for the
    /// subscriber — the fetch window itself is decided by the ViewModel.
    /// </summary>
    public sealed class ChartOlderHistoryRequestedEventArgs(DateTimeOffset oldestVisibleTime) : EventArgs
    {
        /// <summary>The oldest visible bar's open time at the request moment.</summary>
        public DateTimeOffset OldestVisibleTime { get; } = oldestVisibleTime;
    }
}
