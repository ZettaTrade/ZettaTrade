namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
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
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Resources;

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

    /// <summary>Font size of the localized no-volume overlays note (T-093): smaller than the no-data message — the note talks over rendered candles, not an empty panel.</summary>
    private const float NoVolumeNoteFontSize = 13f;

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

    /// <summary>
    /// The localized no-volume state note (T-093): what the overlays print
    /// when the visible candles carry no volume at all — never a fabricated
    /// zero or a spread shape. Same resource-catalog pattern as
    /// <see cref="NoDataMessageKey"/>.
    /// </summary>
    private const string NoVolumeMessageKey = "Chart.NoVolumeData";

    /// <summary>
    /// The bucket count the volume profile ladder (T-093) cuts the visible
    /// window's price spread into: plenty of resolution at terminal zooms
    /// while every bucket stays a wide band on a coarse frame.
    /// </summary>
    private const int VolumeProfileBucketCount = 24;

    /// <summary>
    /// How far across the plot a max-volume histogram bar may reach: a
    /// quarter of the plot width, the terminal right-margin look that keeps
    /// the candles readable.
    /// </summary>
    private const double VolumeProfileMaxWidthRatio = 0.25d;

    /// <summary>Smallest pixel height a volume bar keeps on screen (presentation only).</summary>
    private const float VolumeProfileMinBarHeightPx = 1f;

    /// <summary>Alpha (0–255) of the translucent volume profile bars.</summary>
    private const byte VolumeProfileFillAlpha = 90;

    /// <summary>Smallest pixel width a positive-volume profile bar keeps on screen (presentation only).</summary>
    private const float VolumeProfileMinBarWidthPx = 1f;

    /// <summary>
    /// Stroke width of the line/area price polyline (T-089), in pixels: the
    /// same overlay-line weight the indicator polylines keep, so the price
    /// line reads as chart content next to them.
    /// </summary>
    private const float PriceLineStrokePx = 2f;

    /// <summary>Alpha (0–255) of the area chart's translucent fill under the close polyline.</summary>
    private const byte AreaFillAlpha = 64;

    /// <summary>
    /// Half the length of the open/close ticks an OHLC bars drawing keeps
    /// around its vertical stem: half the candle body width on each side,
    /// so the bars type and the candlesticks type read as the same family.
    /// </summary>
    private const double OhlcTickHalfRatio = CandleBodyWidthRatio / 2d;

    /// <summary>Alpha (0–255) of the VWAP ±1σ band strokes; the center rides the theme indicator color at full alpha.</summary>
    private const byte VwapBandAlpha = 150;

    /// <summary>Stroke widths of the VWAP overlay lines (center, bands), in pixels.</summary>
    private const float VwapCenterStrokePx = 2f;
    private const float VwapBandStrokePx = 1f;

    /// <summary>
    /// Dash pattern (on/off pixels) of the VWAP ±1σ band strokes.
    /// </summary>
    private static readonly float[] VwapBandDashPatternPx = [4f, 3f];

    /// <summary>
    /// Half the base width of a strategy-signal marker (T-112), in pixels:
    /// the marker's triangle keeps a candle-body-scaled look at every zoom
    /// without following the pitch (a fixed size reads as the same sign at
    /// candle and terminal zooms alike).
    /// </summary>
    private const float SignalMarkerHalfWidthPx = 5f;

    /// <summary>Height of a strategy-signal marker's triangle, in pixels (see <see cref="SignalMarkerHalfWidthPx"/>).</summary>
    private const float SignalMarkerHeightPx = 7f;

    /// <summary>
    /// Stroke widths of the footprint overlays (T-122): the position entry
    /// line strokes heavier than the working-order dashes so the entry
    /// reads as the YOUR-position boundary, the order lines stay light —
    /// the same family weights the VWAP center and bands keep.
    /// </summary>
    private const float FootprintEntryStrokePx = 2f;
    private const float FootprintOrderStrokePx = 1.5f;

    /// <summary>Dash pattern (on/off pixels) of the working-order price lines (T-122).</summary>
    private static readonly float[] FootprintOrderDashPatternPx = [6f, 4f];

    /// <summary>Dimensions of the entry line's direction arrow (T-122), in pixels: the signal-marker family's triangle size.</summary>
    private const float FootprintArrowHalfWidthPx = 5f;
    private const float FootprintArrowHeightPx = 7f;

    /// <summary>Horizontal inset of the direction arrow from the plot's left edge, in pixels.</summary>
    private const float FootprintArrowInsetPx = 10f;

    /// <summary>Half width and half height of a fill marker's diamond, in pixels.</summary>
    private const float FillMarkerHalfSizePx = 5f;

    /// <summary>Padding between the PnL tag's text and its background edges, in pixels (the hover pill's own pad).</summary>
    private const float PnlTagPadPx = 3f;

    /// <summary>Inset of the PnL tag's right edge from the plot's right edge, in pixels.</summary>
    private const float PnlTagInsetPx = 4f;

    /// <summary>Decimal places the PnL tag prints its amount with.</summary>
    private const int PnlTagDecimalPlaces = 2;

    /// <summary>Dimensions of the legend's rows (T-122), in pixels.</summary>
    private const float LegendSwatchWidthPx = 16f;
    private const float LegendRowGapPx = 3f;
    private const float LegendPadPx = 8f;
    private const float LegendHalfSwatchHeightPx = 2f;
    private const float LegendTextGapPx = 8f;

    /// <summary>Height of the legend's PnL-tag swatch (the tag's mini silhouette), in pixels.</summary>
    private const float LegendTagSwatchHeightPx = 6f;

    /// <summary>
    /// Stroke width of one compare layer's polyline (T-124): the indicator
    /// overlay family's weight — a neighbor rides the same line family the
    /// workspace's own overlays keep.
    /// </summary>
    private const float CompareLineStrokePx = 2f;

    /// <summary>Alpha (0–255) of the compare chips' translucent backing pill.</summary>
    private const byte CompareChipBackgroundAlpha = 170;

    /// <summary>
    /// The compare chip strip's inset from the plot's top-right corner, in
    /// pixels: the strip lives inside the plot space (it must never cover
    /// the price labels' gutter or the hovered price pill's right rail),
    /// clear of the footprint legend's top-left corner and the OHLC
    /// readout the hover layers print there.
    /// </summary>
    private const float CompareStripInsetPx = 10f;

    /// <summary>
    /// Dimensions and gaps of one compare chip (T-124), in pixels: the
    /// padding around the chip's own content, the inline swatch that
    /// names the layer's color, the gap before the layer's detach square
    /// and that square's cross size — the × press zone is the cross plus
    /// its own padding on every side.
    /// </summary>
    private const float CompareChipPadPx = 5f;
    private const float CompareChipSwatchWidthPx = 12f;
    private const float CompareChipSwatchHeightPx = 3f;
    private const float CompareChipSwatchTextGapPx = 6f;
    private const float CompareChipCrossGapPx = 8f;
    private const float CompareChipCrossHalfPx = 3.5f;
    private const float CompareChipCrossPadPx = 4f;
    private const float CompareChipGapPx = 10f;

    /// <summary>Resource keys published by ThemeManager; the chart mirrors the current theme live.</summary>
    private const string BackgroundColorKey = "Background";
    private const string CandleUpColorKey = "CandleUp";
    private const string CandleDownColorKey = "CandleDown";
    private const string GridColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string IndicatorLineColorKey = "IndicatorLine";

    /// <summary>Supersampling factor of the PNG snapshot export (T-094): the frame renders at this multiple of its logical size, so the shared image stays readable when scaled down or zoomed into.</summary>
    private const int ExportScale = 2;

    /// <summary>Font size of the export watermark, in logical pixels.</summary>
    private const float WatermarkFontSize = 14f;

    /// <summary>Inset of the watermark's bottom-right anchor from the frame's edges, in logical pixels.</summary>
    private const float WatermarkInsetPx = 10f;

    /// <summary>Alpha of the watermark text: translucent, so it never buries the frame's own content.</summary>
    private const byte WatermarkAlpha = 140;

    /// <summary>PNG encode quality for the snapshot export (the format's documented lossless top).</summary>
    private const int PngEncodeQuality = 100;

    /// <summary>The reset-and-read counter of render passes (the T-108 HUD's frame tap). Volatile-read/Interlocked discipline: the render thread writes, the HUD's poller reads.</summary>
    private int _frameTapCount;

    #endregion

    #region Styled Properties

    /// <summary>
    /// StyledProperty for Bars collection.
    /// </summary>
    public static readonly StyledProperty<ObservableCollection<Bar>?> BarsProperty =
        AvaloniaProperty.Register<CandlestickChart, ObservableCollection<Bar>?>(nameof(Bars));

    /// <summary>
    /// StyledProperty for the performance HUD's frame tap (T-108): true makes
    /// every render pass count itself, so an attached poller can read the
    /// chart's frames-per-second. The default false keeps the render pass's
    /// whole overhead at one property read — no counters, no events, nothing
    /// to see for a HUD that is off.
    /// </summary>
    public static readonly StyledProperty<bool> FrameTapEnabledProperty =
        AvaloniaProperty.Register<CandlestickChart, bool>(nameof(FrameTapEnabled));

    /// <summary>
    /// Gets or sets the collection of bars to render.
    /// </summary>
    public ObservableCollection<Bar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>
    /// Gets or sets the performance HUD's frame tap (T-108): true counts every
    /// render pass into a reset-and-read counter (<see cref="TakeFrameTapCount"/>).
    /// Off (the default) the pass carries no HUD cost at all — the counter
    /// never moves because the branch never opens.
    /// </summary>
    public bool FrameTapEnabled
    {
        get => GetValue(FrameTapEnabledProperty);
        set => SetValue(FrameTapEnabledProperty, value);
    }

    /// <summary>
    /// Read and clear the frame-tap counter (the HUD poller's 1 Hz drain):
    /// the render passes the HUD saw since the previous drain. Off-HUD runs
    /// never call this, and an on-HUD drain of a quiet chart returns zero —
    /// both are honest reads.
    /// </summary>
    /// <returns>The counted render passes since the last drain.</returns>
    public int TakeFrameTapCount() => Interlocked.Exchange(ref _frameTapCount, 0);

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
    /// Whether the time axis prints the 12-hour clock ("11:45 PM"); false —
    /// the default — prints the 24-hour "23:45". The chart owns no
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

    /// <summary>
    /// StyledProperty for the session VWAP overlay (T-093): the computed
    /// <see cref="ZettaTrade.Charting.VwapOverlay"/> the ViewModel publishes
    /// against the loaded series (null while the overlay is off), values
    /// aligned with <see cref="Bars"/> by index. The draw pass slices it
    /// with the visible window; a NoVolumeData state draws the localized
    /// no-volume note instead of lines.
    /// </summary>
    public static readonly StyledProperty<VwapOverlay?> VwapOverlayProperty =
        AvaloniaProperty.Register<CandlestickChart, VwapOverlay?>(
            nameof(VwapOverlay),
            defaultValue: null);

    /// <summary>
    /// StyledProperty for the right-margin volume-at-price histogram
    /// (T-093): the toggle only — the histogram itself computes over the
    /// visible window's bars on every pass, exactly what the lens shows.
    /// </summary>
    public static readonly StyledProperty<bool> VolumeProfileVisibleProperty =
        AvaloniaProperty.Register<CandlestickChart, bool>(nameof(VolumeProfileVisible), defaultValue: true);

    /// <summary>The session VWAP overlay the chart draws over the candles; a change re-inks on the next pass.</summary>
    public VwapOverlay? VwapOverlay
    {
        get => GetValue(VwapOverlayProperty);
        set => SetValue(VwapOverlayProperty, value);
    }

    /// <summary>Whether the right-margin volume-at-price histogram is drawn; a change re-inks on the next pass.</summary>
    public bool VolumeProfileVisible
    {
        get => GetValue(VolumeProfileVisibleProperty);
        set => SetValue(VolumeProfileVisibleProperty, value);
    }

    /// <summary>
    /// StyledProperty for the price-chart rendering type (T-089): the
    /// selector state the ViewModel publishes (candlesticks, line, area,
    /// OHLC bars or Heikin-Ashi). The chart draws every variant off the same
    /// candle source; only the renderer differs — never the data path.
    /// </summary>
    public static readonly StyledProperty<ChartSeriesType> SeriesTypeProperty =
        AvaloniaProperty.Register<CandlestickChart, ChartSeriesType>(
            nameof(SeriesType),
            defaultValue: ChartSeriesType.Candlesticks);

    /// <summary>
    /// StyledProperty for the Heikin-Ashi series (T-089): the computed
    /// HA bars the ViewModel publishes against the loaded series (null
    /// unless the series type is <see cref="ChartSeriesType.HeikinAshi"/>),
    /// aligned with <see cref="Bars"/> by index — the same publish
    /// discipline the VWAP overlay keeps. The draw pass slices it with the
    /// visible window; a stale publish races into null and the frame falls
    /// back to raw candlesticks instead of ever drawing a shifted series.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<Bar>?> HeikinAshiBarsProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<Bar>?>(
            nameof(HeikinAshiBars),
            defaultValue: null);

    /// <summary>The price-chart rendering type; a change re-inks on the next pass.</summary>
    public ChartSeriesType SeriesType
    {
        get => GetValue(SeriesTypeProperty);
        set => SetValue(SeriesTypeProperty, value);
    }

    /// <summary>
    /// StyledProperty for the Y-axis scale mode (T-090): the domain the
    /// price axis maps — the plain price (the standing default), its base-10
    /// logarithm, or the percent change over the window's first visible
    /// close. The pixel pipeline stays linear and maps AXIS values (the
    /// mode's transform of the raw prices, <see cref="ChartPriceTransform"/>);
    /// only the spread, the mapping leaves and the label edges know the
    /// mode — never a parallel rendering path.
    /// </summary>
    public static readonly StyledProperty<PriceScaleMode> ScaleModeProperty =
        AvaloniaProperty.Register<CandlestickChart, PriceScaleMode>(
            nameof(ScaleMode),
            defaultValue: PriceScaleMode.Linear);

    /// <summary>The Y-axis scale mode; a change re-inks on the next pass.</summary>
    public PriceScaleMode ScaleMode
    {
        get => GetValue(ScaleModeProperty);
        set => SetValue(ScaleModeProperty, value);
    }

    /// <summary>The Heikin-Ashi series the chart draws while the series type is Heikin-Ashi; a change re-inks on the next pass.</summary>
    public IReadOnlyList<Bar>? HeikinAshiBars
    {
        get => GetValue(HeikinAshiBarsProperty);
        set => SetValue(HeikinAshiBarsProperty, value);
    }

    /// <summary>
    /// StyledProperty for the strategy-signal markers (T-112): the marker
    /// set the ViewModel publishes per loaded series (one entry per bar a
    /// signal stands on). The draw pass resolves every marker's OWN candle
    /// inside the visible window by open time and anchors the marker at
    /// that candle's close — a marker can never slide onto a neighboring
    /// candle, and a marker whose candle left the window simply draws not.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<SignalMarker>> SignalMarkersProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<SignalMarker>>(
            nameof(SignalMarkers),
            defaultValue: []);

    /// <summary>The strategy-signal markers overlaid at the candles' closes; a change re-inks on the next pass.</summary>
    public IReadOnlyList<SignalMarker> SignalMarkers
    {
        get => GetValue(SignalMarkersProperty);
        set => SetValue(SignalMarkersProperty, value);
    }

    /// <summary>
    /// StyledProperty for the trading position's footprint (T-122): the
    /// entry line, direction arrow and floating PnL tag the chart draws
    /// for the workspace symbol's own position (null while none stands).
    /// The draw pass anchors the line at the mapped entry price through
    /// the shared price scale — never a re-scale of the axis.
    /// </summary>
    public static readonly StyledProperty<PositionOverlay?> TradingPositionProperty =
        AvaloniaProperty.Register<CandlestickChart, PositionOverlay?>(
            nameof(TradingPosition),
            defaultValue: null);

    /// <summary>The workspace symbol's position footprint; a change re-inks on the next pass.</summary>
    public PositionOverlay? TradingPosition
    {
        get => GetValue(TradingPositionProperty);
        set => SetValue(TradingPositionProperty, value);
    }

    /// <summary>
    /// StyledProperty for the workspace symbol's working-order lines
    /// (T-122): one dashed price line per order, styled by side.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<WorkingOrderLine>> TradingOrdersProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<WorkingOrderLine>>(
            nameof(TradingOrders),
            defaultValue: []);

    /// <summary>The working-order lines overlaid as dashed price levels; a change re-inks on the next pass.</summary>
    public IReadOnlyList<WorkingOrderLine> TradingOrders
    {
        get => GetValue(TradingOrdersProperty);
        set => SetValue(TradingOrdersProperty, value);
    }

    /// <summary>
    /// StyledProperty for the workspace symbol's executed fills (T-122):
    /// one diamond per execution anchored at the candle the fill time
    /// fell into, resolved against the visible window per pass — the
    /// signal markers' own placement discipline.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<FillMarker>> TradingFillsProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<FillMarker>>(
            nameof(TradingFills),
            defaultValue: []);

    /// <summary>The fill markers overlaid at their candles; a change re-inks on the next pass.</summary>
    public IReadOnlyList<FillMarker> TradingFills
    {
        get => GetValue(TradingFillsProperty);
        set => SetValue(TradingFillsProperty, value);
    }

    /// <summary>
    /// StyledProperty for the symbol-compare layers (T-124): the extra
    /// series the ViewModel publishes, each with its own chip label and
    /// fetched series. Empty — the default — leaves the single-series
    /// pipeline untouched; the draw pass aligns every layer against the
    /// visible window on each pass (never a mutation of the layers).
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<ChartCompareLayer>> CompareLayersProperty =
        AvaloniaProperty.Register<CandlestickChart, IReadOnlyList<ChartCompareLayer>>(
            nameof(CompareLayers),
            defaultValue: []);

    /// <summary>
    /// StyledProperty for the main series' workspace label (T-124): the
    /// legend chip strip names the workspace series first; empty while no
    /// symbol stands (the strip only paints with compare content anyway).
    /// </summary>
    public static readonly StyledProperty<string> MainSeriesLabelProperty =
        AvaloniaProperty.Register<CandlestickChart, string>(
            nameof(MainSeriesLabel),
            defaultValue: string.Empty);

    /// <summary>The symbol-compare layers drawn against the main series; a change re-inks on the next pass.</summary>
    public IReadOnlyList<ChartCompareLayer> CompareLayers
    {
        get => GetValue(CompareLayersProperty);
        set => SetValue(CompareLayersProperty, value);
    }

    /// <summary>The main series' workspace label the legend chip strip prints.</summary>
    public string MainSeriesLabel
    {
        get => GetValue(MainSeriesLabelProperty);
        set => SetValue(MainSeriesLabelProperty, value);
    }

    #endregion

    #region Fields & Properties

    private int _visibleBars = DefaultVisibleBars;

    /// <summary>
    /// The bar pitch the last draw pass painted with (BUG-048): the draw
    /// pass computes it from the plot width (bounds minus the measured
    /// price gutter), a value the pan step cannot re-derive without the
    /// fonts, so the pan consumes this cached constant instead — one bar
    /// of drag distance moves the window exactly one painted slot. The
    /// pitch stands at the cap until the first paint, when nothing was
    /// painted yet. The write happens on the render thread, the read on
    /// the UI thread: the pan step never sees anything worse than the
    /// previous frame's pitch — the very value it is meant to par with.
    /// </summary>
    private double _paintedBarPitch = MaxBarPitchPx;

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
    /// The compare chips' detach press zones (T-124) as the render thread
    /// cached them for the latest frame: one labeled rect per removable
    /// layer, empty whenever the frame carried none (a single-series frame
    /// arms no chip). The write happens on the render thread, the read on
    /// the UI thread (the pointer press) — the same cross-thread
    /// discipline as <see cref="_paintedBarPitch"/>: the worst the press
    /// ever sees is the previous frame's zones, never a torn read.
    /// </summary>
    private CompareChipHit[] _compareChipRects = [];

    /// <summary>
    /// The compare chips' removal requests stream sink (hot; see
    /// <see cref="CompareRemovals"/>).
    /// </summary>
    private readonly Subject<string> _compareRemovals = new();

    /// <summary>
    /// The compare chips' removal requests stream (T-124), hot and raised
    /// on the UI thread: a press inside a chip's cached detach zone emits
    /// the chip's own label (the compared symbol's normalized name). The
    /// layer list lives in the chart ViewModel — the chart only reports
    /// which chip was pressed, the removal and the re-ink route back
    /// through the property the subscriber re-publishes.
    /// </summary>
    public IObservable<string> CompareRemovals => _compareRemovals;

    /// <summary>One compare chip's detach press zone: whom it removes and where it sits in control space.</summary>
    private readonly record struct CompareChipHit(string Label, SKRect Bounds);

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

    /// <summary>
    /// The bars the frame currently paints (T-094): the visible window
    /// resolved from the pan offset and the zoom width over the loaded
    /// series — the same slice <see cref="CreateSnapshot"/> copies for the
    /// draw pass. The visible-range CSV export hands exactly this slice to
    /// the writer, so the file never carries a bar the chart did not show.
    /// </summary>
    public IReadOnlyList<Bar> VisibleBarsSlice
    {
        get
        {
            var (visible, _, _) = CopyVisibleWindow(Bars);
            return visible;
        }
    }

    /// <summary>
    /// Copy the visible window of a series (the slice the render pass
    /// consumes): the span resolved from the pan state, oldest first. An
    /// empty series yields an empty window.
    /// </summary>
    private (Bar[] Visible, int SpanStart, int SpanEnd) CopyVisibleWindow(
        ObservableCollection<Bar>? bars)
    {
        if (bars is not { Count: > 0 })
        {
            return ([], 0, 0);
        }

        var (spanStart, spanEnd) = ChartPanWindow.VisibleSpan(bars.Count, _panOffset, _visibleBars);

        var visible = new Bar[spanEnd - spanStart];
        for (var i = 0; i < visible.Length; i++)
        {
            visible[i] = bars[spanStart + i];
        }

        return (visible, spanStart, spanEnd);
    }

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

        // The T-093 overlays: a new VWAP publish or a volume-profile toggle
        // re-inks on the next pass the same way.
        VwapOverlayProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        VolumeProfileVisibleProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        // The T-089 series type: a selector flip or a new Heikin-Ashi
        // publish re-inks on the next pass the same way.
        SeriesTypeProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        HeikinAshiBarsProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        // The T-112 signal markers: a new marker publish re-inks the same way.
        SignalMarkersProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        // The T-122 footprint: a new position, a new order set or a new
        // fill set re-inks the same way.
        TradingPositionProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));
        TradingOrdersProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));
        TradingFillsProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));

        // The T-124 compare overlays: a new layer list or a main-label
        // update re-inks the same way (the chips ride the label).
        CompareLayersProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));
        MainSeriesLabelProperty.Changed.AddClassHandler<CandlestickChart>(
            (x, _) => Dispatcher.UIThread.Post(x.InvalidateVisual));
    }

    public CandlestickChart()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    #endregion

    #region Accessibility

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new CandlestickChartAutomationPeer(this);

    /// <summary>
    /// The chart's accessibility peer (T-110): the widget is a Skia canvas
    /// with no natural peers (the base Control serves an automation-none
    /// peer, which removes the drawn series from the assistive tree), so
    /// this one restores visibility and names the chart for its current
    /// state — the loaded symbol, working as the compare series' honest
    /// disclosure when one rides.
    /// </summary>
    private sealed class CandlestickChartAutomationPeer(CandlestickChart owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            var attached = base.GetNameCore();
            if (!string.IsNullOrWhiteSpace(attached))
            {
                return attached;
            }

            var (noSeries, series, withCompare) = WidgetAccessibilityNames.ChartTemplates();
            var bars = owner.Bars;
            var layers = owner.CompareLayers;
            var compareLabels = layers is { Count: > 0 }
                ? string.Join(", ", layers.Select(layer => layer.Label))
                : null;

            return WidgetAccessibilityNames.ChartName(
                noSeries,
                series,
                withCompare,
                bars is { Count: > 0 },
                owner.MainSeriesLabel,
                compareLabels);
        }
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

        // The T-108 HUD's frame tap: the off-HUD cost of the whole feature is
        // this one property read (the styled-property cache) — nothing else
        // in the pass knows the HUD exists.
        if (FrameTapEnabled)
            Interlocked.Increment(ref _frameTapCount);

        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
            return;

        // The draw operation runs later, on the render thread: it must only see an immutable snapshot,
        // never the live (UI-thread) Bars collection.
        context.Custom(new CandlestickDrawOperation(new Rect(size), CreateSnapshot(), this));
    }

    /// <summary>
    /// Copies only the visible window of bars (and the colors) so the render thread works on its own data.
    /// Assumes <see cref="Bar"/> instances are not mutated after they are added to the collection.
    /// </summary>
    private ChartSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        var bars = Bars;
        var (visible, spanStart, spanEnd) = CopyVisibleWindow(bars);

        var heikinAshi = SliceHeikinAshiBars(bars, spanStart, spanEnd);
        var closesOnly = SeriesType is ChartSeriesType.Line or ChartSeriesType.Area;
        var drawnBars = DrawnSeries(visible, heikinAshi, SeriesType);
        var compares = ResolveCompareFrames(visible);

        var floorCandidates = SpreadCandidates(drawnBars, closesOnly);
        foreach (var frame in compares)
        {
            foreach (var value in frame.Aligned)
            {
                if (value is { } close)
                {
                    floorCandidates.Add(close);
                }
            }
        }

        return new ChartSnapshot(
            visible,
            SliceIndicatorLines(bars, spanStart, spanEnd),
            SliceVwapOverlay(bars, spanStart, spanEnd),
            _backgroundColor,
            _bullishColor,
            _bearishColor,
            _gridColor,
            _textColor,
            _indicatorLineColor,
            ChartTickCulture.Resolve(),
            Use12HourClock,
            _hoverPosition,
            VolumeProfileVisible,
            SeriesType,
            heikinAshi,
            ChartSignalLayout.Resolve(SignalMarkers, visible),
            TradingPosition,
            TradingOrders,
            ChartFootprintLayout.Resolve(TradingFills, visible),
            ScaleMode,
            visible.Length > 0 ? visible[0].Close : 0m,
            ChartPriceTransform.PositiveFloor(floorCandidates),
            MainSeriesLabel,
            compares);
    }

    /// <summary>
    /// Resolve the compare layers (T-124) against the visible window:
    /// every layer aligns by open time, a layer whose alignment shares no
    /// slot with the window draws nothing at all (an honest nothing, never
    /// a fabricated line), and the legend colors resolve per layer index —
    /// the theme's compare slots first, the fixed fallbacks behind them.
    /// An over-cap layer list is sliced (the ViewModel refuses the fifth
    /// pick; the resolve never trusts it).
    /// </summary>
    private IReadOnlyList<CompareFrame> ResolveCompareFrames(Bar[] visible)
    {
        var layers = CompareLayers;
        if (layers.Count == 0 || visible.Length == 0)
        {
            return [];
        }

        var count = Math.Min(layers.Count, ChartCompareSeries.MaxCompareLayers);
        var frames = new List<CompareFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var layer = layers[i];
            var aligned = ChartCompareSeries.Align(layer.Bars, visible);
            if (aligned.All(static value => value is null))
            {
                continue;
            }

            frames.Add(new CompareFrame(
                layer.Label,
                ChartCompareColor(i),
                ChartCompareSeries.AxisAnchor(aligned),
                aligned));
        }

        return frames;
    }

    /// <summary>One compare layer's legend color: the theme slot the layer index addresses, the fallback set behind a theme that carries none.</summary>
    private SKColor ChartCompareColor(int layerIndex) => ResourceColor(
        ChartCompareSeries.ColorKeys[layerIndex % ChartCompareSeries.ColorKeys.Length],
        ChartCompareSeries.FallbackColors[layerIndex % ChartCompareSeries.FallbackColors.Length]);

    /// <summary>
    /// The drawn series of one frame (T-089's rule, shared with the draw
    /// pass so the scale-mode floor guards the SAME geometry the frame
    /// paints): Heikin-Ashi draws the aligned HA slice; every other type
    /// draws the raw window slice. A stale HA publish races into the raw
    /// fallback for that one frame.
    /// </summary>
    private static IReadOnlyList<Bar> DrawnSeries(
        IReadOnlyList<Bar> visibleBars,
        Bar[]? heikinAshi,
        ChartSeriesType seriesType) =>
        seriesType == ChartSeriesType.HeikinAshi && heikinAshi is { Length: > 0 }
            ? heikinAshi
            : visibleBars;

    /// <summary>
    /// The drawn window's price candidates the scale-mode floor guards
    /// (T-090): the same geometry <see cref="ChartSeriesSpread.Of"/> scans —
    /// the lows and highs, or the closes for the close-line variants (the
    /// wicks are not part of the drawn geometry there).
    /// </summary>
    private static List<decimal> SpreadCandidates(IReadOnlyList<Bar> bars, bool closesOnly)
    {
        var candidates = new List<decimal>(bars.Count * 2);
        foreach (var bar in bars)
        {
            if (closesOnly)
            {
                candidates.Add(bar.Close);
            }
            else
            {
                candidates.Add(bar.Low);
                candidates.Add(bar.High);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Slice the VWAP overlay with the SAME [start, end) window the bars
    /// slice uses (T-093). An overlay whose values no longer align with the
    /// current series (a stale publish racing the new Bars instance) is
    /// skipped wholesale — the alignment rule the indicator lines keep, so
    /// a length mismatch can never reach the draw pass as a silently
    /// shifted polyline.
    /// </summary>
    private VwapOverlay? SliceVwapOverlay(
        ObservableCollection<Bar>? bars,
        int spanStart,
        int spanEnd)
    {
        if (bars is not { Count: > 0 })
        {
            return null;
        }

        var overlay = VwapOverlay;
        if (overlay is null || overlay.Center.Count != bars.Count)
        {
            return null;
        }

        return overlay.Slice(spanStart, spanEnd);
    }

    /// <summary>
    /// Slice the Heikin-Ashi series (T-089) with the SAME [start, end)
    /// window the bars slice uses, while the series type asks for it. A
    /// series whose values no longer align with the current <see cref="Bars"/>
    /// instance (a stale publish racing the new Bars instance) or a
    /// non-Heikin-Ashi frame yields null: the draw pass then falls back to
    /// raw candlesticks for that one frame instead of ever drawing a
    /// silently shifted series (the alignment rule the overlays keep).
    /// </summary>
    private Bar[]? SliceHeikinAshiBars(
        ObservableCollection<Bar>? bars,
        int spanStart,
        int spanEnd)
    {
        if (bars is not { Count: > 0 } || SeriesType != ChartSeriesType.HeikinAshi)
        {
            return null;
        }

        var series = HeikinAshiBars;
        if (series is null || series.Count != bars.Count)
        {
            return null;
        }

        var slice = new Bar[spanEnd - spanStart];
        for (var i = 0; i < slice.Length; i++)
        {
            slice[i] = series[spanStart + i];
        }

        return slice;
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
    /// UI thread (the render thread reads no live chart state — an ambient
    /// CultureInfo there belongs to the process default, not the app), and
    /// the hover position captured from the pointer events on the UI thread
    /// (null when the cursor is outside the control or has left it).
    /// </summary>
    private sealed record ChartSnapshot(
        Bar[] VisibleBars,
        IReadOnlyList<ChartIndicatorLine> Indicators,
        VwapOverlay? Vwap,
        SKColor Background,
        SKColor Bullish,
        SKColor Bearish,
        SKColor Grid,
        SKColor Text,
        SKColor IndicatorLine,
        CultureInfo Culture,
        bool Use12HourClock,
        Point? Hover,
        bool VolumeProfileVisible,
        ChartSeriesType SeriesType,
        Bar[]? HeikinAshi,
        IReadOnlyList<SignalMarkerHit> SignalMarkerHits,
        PositionOverlay? TradingPosition,
        IReadOnlyList<WorkingOrderLine> TradingOrders,
        IReadOnlyList<FillMarkerHit> FillHits,
        PriceScaleMode ScaleMode,
        decimal AxisAnchor,
        decimal AxisFloor,
        string MainSeriesLabel,
        IReadOnlyList<CompareFrame> Compares);

    /// <summary>
    /// One aligned compare layer the draw pass consumes (T-124): the
    /// visible window's closes aligned by open time (null where the
    /// layer's own fetch does not span the slot), the layer's own percent
    /// anchor, and the legend color + chip label. Pure frame-local state —
    /// the render thread reads none of the live layer list.
    /// </summary>
    /// <param name="Label">The compared symbol's normalized name (the chip's label and its removal identity).</param>
    /// <param name="Color">The layer's legend color (the polyline and the chip swatch keep one).</param>
    /// <param name="Anchor">The layer's own percent anchor: its first aligned close.</param>
    /// <param name="Aligned">One slot per visible bar; the slot carries the layer's close or null.</param>
    private sealed record CompareFrame(
        string Label,
        SKColor Color,
        decimal Anchor,
        decimal?[] Aligned);

    /// <summary>
    /// Draws the chart straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class CandlestickDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly ChartSnapshot _snapshot;
        private readonly CandlestickChart _owner;

        public CandlestickDrawOperation(
            Rect bounds,
            ChartSnapshot snapshot,
            CandlestickChart owner)
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

        /// <summary>
        /// The axis-domain value of one raw price (T-090): every pixel
        /// mapping of the draw pass consumes axis values, so each raw price
        /// crosses the mode's transform exactly at its mapping site. The
        /// linear mode keeps the identity — the historical pipeline.
        /// </summary>
        private decimal AxisOf(decimal price) =>
            ChartPriceTransform.ToAxisValue(price, _snapshot.ScaleMode, _snapshot.AxisAnchor, _snapshot.AxisFloor);

        /// <summary>
        /// The axis-domain value of one compare layer's raw close (T-124):
        /// every series maps in the one domain the draw pass paints — the
        /// percent layers through their own anchors (each series rebases to
        /// its own zero), the linear and log layers through the shared
        /// identity/log transform (the anchor is meaningless there). Never
        /// a parallel mapping path: the same <see cref="ChartPriceTransform"/>
        /// wrap every other mapping site consumes.
        /// </summary>
        private decimal AxisOfLayer(decimal price, decimal anchor) =>
            ChartPriceTransform.ToAxisValue(price, _snapshot.ScaleMode, anchor, _snapshot.AxisFloor);

        /// <summary>
        /// The axis tick's label (T-090): the linear mode prints the tick
        /// itself (the historical price label); the log mode prints the
        /// PRICE the log-domain tick unwinds to, with the raw-price decimals
        /// (log-unit digits would be nonsense on a price axis); the percent
        /// mode prints the signed relative read. All culture-aware.
        /// </summary>
        private string FormatAxisTick(decimal tick, int axisDecimals, int rawDecimals) => _snapshot.ScaleMode switch
        {
            PriceScaleMode.Logarithmic => ChartAxisFormatting.FormatPriceTick(
                ChartPriceTransform.ToPrice(
                    tick,
                    PriceScaleMode.Logarithmic,
                    _snapshot.AxisAnchor,
                    _snapshot.AxisFloor),
                rawDecimals,
                _snapshot.Culture),
            PriceScaleMode.Percent => ChartAxisFormatting.FormatPercentTick(tick, axisDecimals, _snapshot.Culture),
            _ => ChartAxisFormatting.FormatPriceTick(tick, axisDecimals, _snapshot.Culture),
        };

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

        /// <summary>
        /// The frame's full render pass (the export rides the SAME pass the
        /// screen paints — the acceptance holds by construction). Assembly-
        /// internal only because the containing class's export calls it
        /// directly; Avalonia itself enters through <see cref="Render"/>.
        /// </summary>
        internal void Draw(SKCanvas canvas)
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

            // The drawn series (T-089): Heikin-Ashi draws the aligned HA
            // slice; every other type draws the raw window slice. A stale
            // HA publish races into the raw fallback for that one frame.
            // The overlays (VWAP, volume-at-price, indicator lines) keep
            // consuming the RAW window — they never follow the renderer
            // variant, so their alignment and semantics stay untouched.
            IReadOnlyList<Bar> drawnBars = DrawnSeries(visibleBars, _snapshot.HeikinAshi, _snapshot.SeriesType);

            // The price spread follows the DRAWN series so nothing the frame
            // paints can clip the plot: candles, OHLC bars and Heikin-Ashi
            // span the full low–high range of their bars, while the line and
            // area variants only touch closes (the terminal close-line
            // scale — the wicks are not part of the drawn geometry).
            var closesOnly = _snapshot.SeriesType is ChartSeriesType.Line or ChartSeriesType.Area;
            var (lowest, highest) = ChartSeriesSpread.Of(drawnBars, closesOnly);

            // The scale-mode domain (T-090): the pixel pipeline stays linear
            // and maps AXIS values — the mode's transform of the raw prices
            // (the log's base-10 logarithm, the percent change over the
            // window's first visible close). The raw read returns only at
            // the label edges (the axis labels, the hover pill, the OHLC
            // readout). A value outside the mode's domain clamps VISIBLY
            // onto the drawn geometry's positive floor — never NaN, never a
            // dropped candle, never a silent mode fallback.
            //
            // The union (T-124): the compare layers' own axis candidates
            // extend the same domain the main series' bounds start — the
            // percent layers map through their OWN anchors (each series
            // rebases to its own zero), the log layers share the floor. A
            // far-priced neighbor stretches the one padded scale instead of
            // clipping off it. An empty layer list never enters the loop:
            // the union IS the historical two-bound call, bit for bit.
            var mainLowAxis = AxisOf(lowest);
            var mainHighAxis = AxisOf(highest);
            var unionMin = Math.Min(mainLowAxis, mainHighAxis);
            var unionMax = Math.Max(mainLowAxis, mainHighAxis);
            foreach (var frame in _snapshot.Compares)
            {
                foreach (var candidate in ChartCompareSeries.LayerAxisCandidates(
                             frame.Aligned,
                             _snapshot.ScaleMode,
                             frame.Anchor,
                             _snapshot.AxisFloor))
                {
                    if (candidate < unionMin)
                    {
                        unionMin = candidate;
                    }

                    if (candidate > unionMax)
                    {
                        unionMax = candidate;
                    }
                }
            }

            var (minAxis, maxAxis, axisRange) = ChartPriceScale.PaddedSpread(unionMin, unionMax);

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

            var axisTicks = ChartAxisFormatting.PickPriceTicks(minAxis, maxAxis, PriceAxisLabelCount);
            var axisDecimals = ChartAxisFormatting.PickPriceDecimals(axisRange / PriceAxisLabelCount);

            // The RAW-price decimals (the OHLC readout, the trading-position
            // tags, the log-mode axis labels): the log axis's own digits are
            // log-unit digits, not price digits, and the raw readout must
            // keep the precision of the values it prints.
            var rawDecimals = ChartAxisFormatting.PickPriceDecimals((highest - lowest) / PriceAxisLabelCount);
            var priceTexts = axisTicks
                .Select(tick => FormatAxisTick(tick, axisDecimals, rawDecimals))
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
            _owner._paintedBarPitch = barPitch;

            DrawGrid(
                canvas,
                gridPaint,
                plotRect,
                minAxis,
                axisRange,
                axisTicks,
                timeTicks,
                barPitch,
                openTimes);

            switch (_snapshot.SeriesType)
            {
                case ChartSeriesType.Bars:
                    DrawOhlcBars(canvas, plotRect, drawnBars, barPitch, minAxis, axisRange);
                    break;

                case ChartSeriesType.Line:
                    DrawCloseLine(canvas, plotRect, drawnBars, barPitch, minAxis, axisRange, filled: false);
                    break;

                case ChartSeriesType.Area:
                    DrawCloseLine(canvas, plotRect, drawnBars, barPitch, minAxis, axisRange, filled: true);
                    break;

                default:
                    // Candlesticks, and Heikin-Ashi once its series slice is
                    // aligned (the raw-slice fallback draws candlesticks).
                    DrawCandles(canvas, plotRect, drawnBars, barPitch, minAxis, axisRange);
                    break;
            }

            // The T-112 signal markers ride right above the drawn series
            // (under the volume profile, the indicator lines, the VWAP and
            // the hover layers) and anchor at the RAW chain's close levels
            // — every drawn variant keeps the raw candle's close inside its
            // own spread, so the markers never leave the plot.
            DrawSignalMarkers(canvas, plotRect, barPitch, visibleBars.Length, minAxis, axisRange);

            // The T-122 footprint rides the same band (my own activity,
            // nothing leaves the process): the execution diamonds, then
            // the dashed order lines, then the entry line with its PnL tag
            // — every level mapped through the shared price scale, and the
            // legend lists exactly the shapes this frame actually drew.
            DrawFillMarkers(canvas, plotRect, barPitch, visibleBars.Length, minAxis, axisRange);
            var orderLineCount = DrawWorkingOrders(canvas, plotRect, minAxis, axisRange);
            var (entryLineDrawn, pnlTagDrawn) = DrawTradingPosition(canvas, plotRect, minAxis, axisRange, rawDecimals);
            DrawFootprintLegend(
                canvas,
                font,
                plotRect,
                entryLineDrawn ? _snapshot.TradingPosition!.Direction : null,
                pnlTagDrawn,
                orderLineCount > 0,
                _snapshot.FillHits.Count > 0);

            // The right-margin volume-at-price histogram (T-093) rides over
            // the candles translucently; the overlay lines draw over it.
            DrawVolumeProfile(canvas, plotRect, visibleBars, minAxis, axisRange);

            // The T-124 compare layers: one percent-rebased polyline per
            // compared symbol over the volume profile, under the indicator
            // and VWAP overlays (a neighbor never outweighs a workspace
            // panel), plus the legend chip strip pinned top-right — the
            // strips' detach squares are the removal route. A single-series
            // frame is byte-exact without the strip: the resolve produced
            // nothing and strip + lines short-circuit.
            DrawCompareLayers(canvas, font, textPaint, plotRect, barPitch, visibleBars.Length, minAxis, axisRange);

            DrawIndicatorLines(canvas, plotRect, visibleBars, barPitch, minAxis, axisRange, _snapshot.Indicators);
            DrawVwapOverlay(canvas, plotRect, visibleBars, barPitch, minAxis, axisRange, _snapshot.Vwap);
            DrawPriceAxis(canvas, font, textPaint, plotRect, minAxis, axisRange, axisTicks, priceTexts);
            DrawTimeAxis(canvas, font, textPaint, plotRect, barPitch, openTimes, timeTicks, timeTexts);

            // The honest no-volume note (T-093): shown when the drawn overlays
            // carry no volume-backed shape at all — the candles' prices still
            // render, the overlays just explain their own absence. It reads
            // the DRAWN series: the Heikin-Ashi transform preserves volumes,
            // so the note's semantics are type-invariant.
            DrawNoVolumeNote(canvas, textPaint, plotRect, drawnBars);

            // The hover layer rides LAST: the crosshair paints over the
            // candles and the axis labels (the terminal look). The hovered
            // readout keys off the DRAWN series — a Heikin-Ashi frame reads
            // its smoothed OHLC, every other type reads the raw bar.
            DrawHover(
                canvas,
                font,
                textPaint,
                plotRect,
                drawnBars,
                barPitch,
                minAxis,
                axisRange,
                axisDecimals,
                rawDecimals,
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
        /// time ticks — mapped onto the SAME bar slots the candles occupy.
        /// Both stay strictly inside the plot, so the gutters and the canvas
        /// edges stay clean.
        /// </summary>
        private static void DrawGrid(
            SKCanvas canvas,
            SKPaint paint,
            Rect plotRect,
            decimal minAxis,
            decimal axisRange,
            IReadOnlyList<decimal> axisTicks,
            IReadOnlyList<DateTimeOffset> timeTicks,
            double barPitch,
            IReadOnlyList<DateTimeOffset> openTimes)
        {
            foreach (var tick in axisTicks)
            {
                var y = ChartPriceScale.PriceToY(plotRect, tick, minAxis, axisRange);
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
            decimal minAxis,
            decimal axisRange)
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

                var openY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Open), minAxis, axisRange);
                var closeY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Close), minAxis, axisRange);
                var highY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.High), minAxis, axisRange);
                var lowY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Low), minAxis, axisRange);

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
        /// Classic OHLC bars (T-089): one vertical stem from the low to the
        /// high per bar slot, a left-hand tick at the open and a right-hand
        /// tick at the close, colored by the bar's own direction exactly the
        /// candle paints are. The ticks keep half the candle body width per
        /// side, so the bars family reads next to the candlesticks family.
        /// </summary>
        private void DrawOhlcBars(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minAxis,
            decimal axisRange)
        {
            var tickHalf = (float)(barPitch * OhlcTickHalfRatio);

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
                var paint = bar.Close >= bar.Open ? bullishPaint : bearishPaint;

                var highY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.High), minAxis, axisRange);
                var lowY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Low), minAxis, axisRange);
                canvas.DrawLine(x, highY, x, lowY, paint);

                var openY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Open), minAxis, axisRange);
                var closeY = ChartPriceScale.PriceToY(plotRect, AxisOf(bar.Close), minAxis, axisRange);
                canvas.DrawLine(x - tickHalf, openY, x, openY, paint);
                canvas.DrawLine(x, closeY, x + tickHalf, closeY, paint);
            }
        }

        /// <summary>
        /// The close-price rendering variants (T-089): a polyline through the
        /// bars' closes over the same bar slots and price scale the candles
        /// ride (the intraday line chart); the area variant fills the region
        /// under the polyline down to the plot's bottom edge translucently
        /// and strokes the line over it. The line's neutral color is the
        /// theme text color — the indicator-accent color stays the overlays'
        /// own, so the price line and the VWAP/SMA lines never blend into
        /// one shape.
        /// </summary>
        private void DrawCloseLine(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minAxis,
            decimal axisRange,
            bool filled)
        {
            // The mapped polyline points serve both the stroked line and the
            // area fill (a frame-local copy the snapshot discipline already
            // pays; the builder walks consume it twice, never the bars).
            var points = new SKPoint[bars.Count];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new SKPoint(
                    (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, bars.Count, i),
                    ChartPriceScale.PriceToY(plotRect, AxisOf(bars[i].Close), minAxis, axisRange));
            }

            if (filled)
            {
                using var fillBuilder = new SKPathBuilder();
                fillBuilder.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++)
                {
                    fillBuilder.LineTo(points[i]);
                }

                // The area closes down to the plot's bottom edge across the
                // two extreme slots: last center, bottom, first center.
                var lastSlotX = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, bars.Count, bars.Count - 1);
                var firstSlotX = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, bars.Count, 0);
                fillBuilder.LineTo(lastSlotX, (float)plotRect.Bottom);
                fillBuilder.LineTo(firstSlotX, (float)plotRect.Bottom);
                fillBuilder.Close();

                using var fillPaint = new SKPaint
                {
                    Color = _snapshot.Text.WithAlpha(AreaFillAlpha),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };

                // The fill first: the stroked line stays the shape's topmost
                // stroke (the same order the VWAP overlay keeps).
                using var filledPath = fillBuilder.Detach();
                canvas.DrawPath(filledPath, fillPaint);
            }

            using var lineBuilder = new SKPathBuilder();
            lineBuilder.MoveTo(points[0]);
            for (var i = 1; i < points.Length; i++)
            {
                lineBuilder.LineTo(points[i]);
            }

            using var linePath = lineBuilder.Detach();
            using var linePaint = new SKPaint
            {
                Color = _snapshot.Text,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = PriceLineStrokePx,
                IsAntialias = true
            };

            canvas.DrawPath(linePath, linePaint);
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
            decimal minAxis,
            decimal axisRange,
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
                    var y = ChartPriceScale.PriceToY(plotRect, AxisOf(value), minAxis, axisRange);

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

                // DrawPath consumes a copy: the detached path is dead
                // right after the call, so the native wrapper must not
                // outlive it for the finalizer (BUG-049 — native memory
                // churn on every redraw for every indicator line).
                using var drawnPath = path.Detach();
                canvas.DrawPath(drawnPath, paint);
            }
        }

        /// <summary>
        /// The session-anchored VWAP overlay (T-093) drawn over the candles:
        /// the session VWAP polyline rides the same bar slots and the same
        /// price scale as the candles — never its own axis — flanked by its
        /// dashed ±1σ bands, and every session break in the visible window
        /// cuts all three polylines (the slice's rebased session starts name
        /// the window's own breaks). A null value breaks the pen — an
        /// unweighted bar never fabricates a segment — and a NoVolumeData
        /// state draws nothing: the localized no-volume note answers the
        /// toggled-but-empty overlay instead of any fabricated line.
        /// </summary>
        private void DrawVwapOverlay(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            double barPitch,
            decimal minAxis,
            decimal axisRange,
            VwapOverlay? overlay)
        {
            if (overlay is null || overlay.State == VwapOverlayState.NoVolumeData)
            {
                return;
            }

            var count = bars.Count;
            if (overlay.Center.Count != count || overlay.Upper.Count != count || overlay.Lower.Count != count)
            {
                // A stale publish racing a Bars rebase: skip wholesale (the
                // snapshot slice guards this, the draw pass never trusts).
                return;
            }

            using var centerBuilder = new SKPathBuilder();
            using var upperBandBuilder = new SKPathBuilder();
            using var lowerBandBuilder = new SKPathBuilder();
            var sessionStarts = overlay.SessionStarts;
            var nextSession = 0;
            var centerPen = false;
            var upperPen = false;
            var lowerPen = false;

            for (var i = 0; i < count; i++)
            {
                // A session break cuts every polyline: the accumulation
                // resets at the boundary and the segments must not join.
                if (nextSession < sessionStarts.Count && sessionStarts[nextSession] == i)
                {
                    centerPen = false;
                    upperPen = false;
                    lowerPen = false;
                    nextSession++;
                }

                var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, count, i);

                if (overlay.Center[i] is { } center)
                {
                    var y = ChartPriceScale.PriceToY(plotRect, AxisOf(center), minAxis, axisRange);
                    if (centerPen)
                    {
                        centerBuilder.LineTo(x, y);
                    }
                    else
                    {
                        centerBuilder.MoveTo(x, y);
                        centerPen = true;
                    }
                }
                else
                {
                    centerPen = false;
                }

                if (overlay.Upper[i] is { } upper)
                {
                    var y = ChartPriceScale.PriceToY(plotRect, AxisOf(upper), minAxis, axisRange);
                    if (upperPen)
                    {
                        upperBandBuilder.LineTo(x, y);
                    }
                    else
                    {
                        upperBandBuilder.MoveTo(x, y);
                        upperPen = true;
                    }
                }
                else
                {
                    upperPen = false;
                }

                if (overlay.Lower[i] is { } lower)
                {
                    var y = ChartPriceScale.PriceToY(plotRect, AxisOf(lower), minAxis, axisRange);
                    if (lowerPen)
                    {
                        lowerBandBuilder.LineTo(x, y);
                    }
                    else
                    {
                        lowerBandBuilder.MoveTo(x, y);
                        lowerPen = true;
                    }
                }
                else
                {
                    lowerPen = false;
                }
            }

            // The center rides the theme's indicator color solid; the ±1σ
            // bands ride the same color dashed and translucent — the dash
            // keeps a band from reading as a second overlay center.
            using var centerPaint = new SKPaint
            {
                Color = _snapshot.IndicatorLine,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = VwapCenterStrokePx,
                IsAntialias = true
            };

            // The dash effect clones across the paint: one native effect
            // serves both bands, disposed with the paint (BUG-049's paint
            // lifetime discipline).
            using var bandDashEffect = SKPathEffect.CreateDash(VwapBandDashPatternPx, 0f);
            using var bandPaint = new SKPaint
            {
                Color = _snapshot.IndicatorLine.WithAlpha(VwapBandAlpha),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = VwapBandStrokePx,
                IsAntialias = true,
                PathEffect = bandDashEffect
            };

            using var centerPath = centerBuilder.Detach();
            using var upperBandPath = upperBandBuilder.Detach();
            using var lowerBandPath = lowerBandBuilder.Detach();

            // The bands first: the center stays the topmost stroke of the
            // three (the terminal look: the value over its spread).
            canvas.DrawPath(upperBandPath, bandPaint);
            canvas.DrawPath(lowerBandPath, bandPaint);
            canvas.DrawPath(centerPath, centerPaint);
        }

        /// <summary>
        /// The strategy-signal markers (T-112): one small triangle per
        /// resolved hit, anchored at its own candle's close on the shared
        /// price scale and centered on that candle's bar slot on the shared
        /// bar layout — placement is exact by construction, not an
        /// approximation. A long stance points up above the close; a flat
        /// stance points down below it; both keep the close as the
        /// triangle's base, so the apex alone crosses away from the level
        /// the strategy acted at. With no markers resolved for the visible
        /// window, nothing draws.
        /// </summary>
        private void DrawSignalMarkers(
            SKCanvas canvas,
            Rect plotRect,
            double barPitch,
            int barCount,
            decimal minAxis,
            decimal axisRange)
        {
            var hits = _snapshot.SignalMarkerHits;
            if (hits.Count == 0)
            {
                return;
            }

            // The marker rides the candle colors: a long is bullish, a flat
            // is bearish — the stance's direction read, straight from the
            // same palette the candles use (no separate theme color).
            using var longPaint = new SKPaint
            {
                Color = _snapshot.Bullish,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            using var flatPaint = new SKPaint
            {
                Color = _snapshot.Bearish,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            foreach (var hit in hits)
            {
                var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, barCount, hit.Slot);
                var y = ChartPriceScale.PriceToY(plotRect, AxisOf(hit.Close), minAxis, axisRange);

                // The close is the base; the apex rises above it (long) or
                // hangs below it (flat) — the shape flips, the anchor never
                // moves.
                var apex = hit.Role == SignalMarkerRole.Long
                    ? y - SignalMarkerHeightPx
                    : y + SignalMarkerHeightPx;

                using var builder = new SKPathBuilder();
                builder.MoveTo(new SKPoint(x - SignalMarkerHalfWidthPx, (float)y));
                builder.LineTo(new SKPoint(x, (float)apex));
                builder.LineTo(new SKPoint(x + SignalMarkerHalfWidthPx, (float)y));
                builder.Close();

                // BUG-049's lifetime discipline: the builder detaches to the
                // path actually drawn and both are disposed explicitly.
                using var drawn = builder.Detach();
                canvas.DrawPath(drawn, hit.Role == SignalMarkerRole.Long ? longPaint : flatPaint);
            }
        }

        /// <summary>
        /// The execution fill markers (T-122): one small diamond per
        /// resolved hit, anchored at ITS OWN candle's slot center and at
        /// the fill's own price on the shared scales — the containment the
        /// resolve pass pinned. A diamond whose price left the plotted
        /// range draws not (an absence); a buy rides the bullish palette,
        /// a sell the bearish one, right from the candles' own colors.
        /// </summary>
        private void DrawFillMarkers(
            SKCanvas canvas,
            Rect plotRect,
            double barPitch,
            int barCount,
            decimal minAxis,
            decimal axisRange)
        {
            var hits = _snapshot.FillHits;
            if (hits.Count == 0)
            {
                return;
            }

            using var buyPaint = new SKPaint
            {
                Color = _snapshot.Bullish,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            using var sellPaint = new SKPaint
            {
                Color = _snapshot.Bearish,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            foreach (var hit in hits)
            {
                var y = ChartFootprintLayout.LevelY(plotRect, AxisOf(hit.Price), minAxis, axisRange);
                if (y is not { } markerY)
                {
                    continue;
                }

                var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, barCount, hit.Slot);

                using var builder = new SKPathBuilder();
                builder.MoveTo(new SKPoint(x, (float)markerY - FillMarkerHalfSizePx));
                builder.LineTo(new SKPoint(x + FillMarkerHalfSizePx, (float)markerY));
                builder.LineTo(new SKPoint(x, (float)markerY + FillMarkerHalfSizePx));
                builder.LineTo(new SKPoint(x - FillMarkerHalfSizePx, (float)markerY));
                builder.Close();

                using var drawn = builder.Detach();
                canvas.DrawPath(drawn, hit.Side == OrderSide.Buy ? buyPaint : sellPaint);
            }
        }

        /// <summary>
        /// The working-order price lines (T-122): one full-width dashed
        /// level per price-bearing order, styled by side (buy rides the
        /// bullish palette, sell the bearish one) at EXACTLY the mapped
        /// order price. A level outside the plotted range draws not; the
        /// returned count feeds the legend (only drawn shapes explained).
        /// </summary>
        private int DrawWorkingOrders(
            SKCanvas canvas,
            Rect plotRect,
            decimal minAxis,
            decimal axisRange)
        {
            var orders = _snapshot.TradingOrders;
            if (orders.Count == 0)
            {
                return 0;
            }

            using var dashEffect = SKPathEffect.CreateDash(FootprintOrderDashPatternPx, 0f);
            using var buyPaint = new SKPaint
            {
                Color = _snapshot.Bullish,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = FootprintOrderStrokePx,
                IsAntialias = true,
                PathEffect = dashEffect
            };

            using var sellPaint = new SKPaint
            {
                Color = _snapshot.Bearish,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = FootprintOrderStrokePx,
                IsAntialias = true,
                PathEffect = dashEffect
            };

            var drawnLines = 0;
            foreach (var order in orders)
            {
                var y = ChartFootprintLayout.LevelY(plotRect, AxisOf(order.Price), minAxis, axisRange);
                if (y is not { } lineY)
                {
                    continue;
                }

                canvas.DrawLine((float)plotRect.Left, (float)lineY, (float)plotRect.Right, (float)lineY, order.Side == OrderSide.Buy ? buyPaint : sellPaint);
                drawnLines++;
            }

            return drawnLines;
        }

        /// <summary>
        /// The position's own footprint (T-122): the full-width entry line
        /// at the mapped average entry with a direction arrow at the line's
        /// left end (an up-triangle for a long, a down-triangle for a
        /// short), plus the floating PnL tag — a rounded price pill at the
        /// plot's right edge, snapped to the latest observed price and
        /// clamped into the visible band by the layout (a fast move pins
        /// the tag at the edge instead of painting into the gutters). The
        /// pair reports what this frame actually drew, for the legend.
        /// </summary>
        private (bool EntryLineDrawn, bool PnlTagDrawn) DrawTradingPosition(
            SKCanvas canvas,
            Rect plotRect,
            decimal minAxis,
            decimal axisRange,
            int rawDecimals)
        {
            var position = _snapshot.TradingPosition;
            if (position is null)
            {
                return (false, false);
            }

            var entryColor = position.Direction == PositionDirection.Long ? _snapshot.Bullish : _snapshot.Bearish;

            // The entry line: drawn only while its level sits inside the
            // plotted range (an honest absence otherwise).
            var entryDrawn = false;
            if (ChartFootprintLayout.LevelY(plotRect, AxisOf(position.EntryPrice), minAxis, axisRange) is { } entryY)
            {
                using var entryPaint = new SKPaint
                {
                    Color = entryColor,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = FootprintEntryStrokePx,
                    IsAntialias = true
                };

                canvas.DrawLine((float)plotRect.Left, (float)entryY, (float)plotRect.Right, (float)entryY, entryPaint);

                // The direction arrow rides the line's left end: the base
                // sits ON the line so the apex alone crosses away from the
                // entry level (the signal markers' own base discipline).
                var arrowX = (float)(plotRect.Left + FootprintArrowInsetPx);
                var apexY = position.Direction == PositionDirection.Long
                    ? entryY - FootprintArrowHeightPx
                    : entryY + FootprintArrowHeightPx;

                using var arrowBuilder = new SKPathBuilder();
                arrowBuilder.MoveTo(new SKPoint(arrowX - FootprintArrowHalfWidthPx, (float)entryY));
                arrowBuilder.LineTo(new SKPoint(arrowX, (float)apexY));
                arrowBuilder.LineTo(new SKPoint(arrowX + FootprintArrowHalfWidthPx, (float)entryY));
                arrowBuilder.Close();

                using var arrowPaint = new SKPaint
                {
                    Color = entryColor,
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };

                using var arrow = arrowBuilder.Detach();
                canvas.DrawPath(arrow, arrowPaint);
                entryDrawn = true;
            }

            // The floating PnL tag: only when the feed has observed a
            // price (the engine never invents one, so the tag never does
            // either); the layout clamps it into the visible band.
            var tagDrawn = false;
            if (position.LatestPrice is { } latest)
            {
                var tagText = position.UnrealizedPnl.Amount.ToString(
                    "F" + PnlTagDecimalPlaces.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _snapshot.Culture);

                using var tagFont = new SKFont(SKTypeface.Default, AxisFontSize);
                var textWidth = tagFont.MeasureText(tagText);
                var textHeight = -tagFont.Metrics.Ascent + tagFont.Metrics.Descent;
                var tagWidth = textWidth + (2f * PnlTagPadPx);
                var tagHeight = textHeight + (2f * PnlTagPadPx);
                var tagY = (float)ChartFootprintLayout.TagY(plotRect, AxisOf(latest), minAxis, axisRange);

                using var tagPaint = new SKPaint
                {
                    Color = entryColor,
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };

                var tagRect = new SKRect(
                    (float)plotRect.Right - PnlTagInsetPx - tagWidth,
                    tagY - (tagHeight / 2f),
                    (float)plotRect.Right - PnlTagInsetPx,
                    tagY + (tagHeight / 2f));
                var tagRadius = CornerRadiusFor(tagHeight);
                canvas.DrawRoundRect(tagRect, tagRadius, tagRadius, tagPaint);

                using var tagTextPaint = new SKPaint
                {
                    Color = _snapshot.Background,
                    IsAntialias = true
                };

                canvas.DrawText(
                    tagText,
                    (float)plotRect.Right - PnlTagInsetPx - PnlTagPadPx,
                    tagY - ((tagFont.Metrics.Ascent + tagFont.Metrics.Descent) / 2f),
                    SKTextAlign.Right,
                    tagFont,
                    tagTextPaint);
                tagDrawn = true;
            }

            return (entryDrawn, tagDrawn);
        }

        /// <summary>The tag's corner radius: a half-height rounded pill cap.</summary>
        private static float CornerRadiusFor(float tagHeight) => tagHeight / 2f;

        /// <summary>
        /// The footprint legend (T-122): the top-left rows exactly the
        /// shapes this frame DREW (every drawn shape explained, nothing
        /// else listed — the pure
        /// <see cref="ChartFootprintLegend.Rows"/> compose decides), one
        /// swatch per shape side by side with its localized explanation.
        /// No drawn shape keeps the corner clean.
        /// </summary>
        private void DrawFootprintLegend(
            SKCanvas canvas,
            SKFont font,
            Rect plotRect,
            PositionDirection? entryDirection,
            bool pnlTagDrawn,
            bool orderLinesDrawn,
            bool fillMarkersDrawn)
        {
            var rows = ChartFootprintLegend.Rows(entryDirection, pnlTagDrawn, orderLinesDrawn, fillMarkersDrawn);
            if (rows.Count == 0)
            {
                return;
            }

            var textHeight = -font.Metrics.Ascent + font.Metrics.Descent;
            var x = (float)(plotRect.Left + LegendPadPx);
            var y = (float)(plotRect.Top + LegendPadPx) - font.Metrics.Ascent;

            foreach (var row in rows)
            {
                // The swatch sits vertically centered on the row.
                var swatchY = y + ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);
                DrawLegendSwatch(canvas, row.Shape, x, swatchY);
                canvas.DrawText(
                    GetFootprintLegendText(row.Key),
                    x + LegendSwatchWidthPx + LegendTextGapPx,
                    y,
                    SKTextAlign.Left,
                    font,
                    new SKPaint { Color = _snapshot.Text, IsAntialias = true });

                y += textHeight + LegendRowGapPx;
            }
        }

        /// <summary>Paint one legend swatch (the mini shape the row explains).</summary>
        private void DrawLegendSwatch(SKCanvas canvas, FootprintLegendShape shape, float x, float centerY)
        {
            switch (shape)
            {
                case FootprintLegendShape.EntryLine:
                    using (var paint = new SKPaint
                    {
                        Color = _snapshot.IndicatorLine,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = FootprintEntryStrokePx,
                        IsAntialias = true
                    })
                    {
                        canvas.DrawLine(x, centerY, x + LegendSwatchWidthPx, centerY, paint);
                    }

                    using (var arrow = new SKPathBuilder())
                    {
                        arrow.MoveTo(new SKPoint(x + (LegendSwatchWidthPx / 2f), centerY - LegendHalfSwatchHeightPx - FootprintArrowHeightPx));
                        arrow.LineTo(new SKPoint(x + (LegendSwatchWidthPx / 2f) - LegendHalfSwatchHeightPx, centerY - LegendHalfSwatchHeightPx));
                        arrow.LineTo(new SKPoint(x + (LegendSwatchWidthPx / 2f) + LegendHalfSwatchHeightPx, centerY - LegendHalfSwatchHeightPx));
                        arrow.Close();

                        using var arrowPath = arrow.Detach();
                        using var arrowPaint = new SKPaint
                        {
                            Color = _snapshot.IndicatorLine,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true
                        };
                        canvas.DrawPath(arrowPath, arrowPaint);
                    }

                    break;

                case FootprintLegendShape.PnlTag:
                    using (var paint = new SKPaint
                    {
                        Color = _snapshot.IndicatorLine,
                        Style = SKPaintStyle.Fill,
                        IsAntialias = true
                    })
                    {
                        var swatchTop = centerY - LegendHalfSwatchHeightPx;
                        var swatchRect = new SKRect(
                            x,
                            swatchTop,
                            x + LegendSwatchWidthPx,
                            swatchTop + LegendTagSwatchHeightPx);
                        var swatchRadius = CornerRadiusFor(LegendTagSwatchHeightPx);
                        canvas.DrawRoundRect(swatchRect, swatchRadius, swatchRadius, paint);
                    }

                    break;

                case FootprintLegendShape.OrderLine:
                    using (var dashEffect = SKPathEffect.CreateDash(FootprintOrderDashPatternPx, 0f))
                    using (var paint = new SKPaint
                    {
                        Color = _snapshot.Text,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = FootprintOrderStrokePx,
                        IsAntialias = true,
                        PathEffect = dashEffect
                    })
                    {
                        canvas.DrawLine(x, centerY, x + LegendSwatchWidthPx, centerY, paint);
                    }

                    break;

                case FootprintLegendShape.FillMarker:
                    using (var builder = new SKPathBuilder())
                    {
                        var cx = x + (LegendSwatchWidthPx / 2f);
                        builder.MoveTo(new SKPoint(cx, centerY - (LegendHalfSwatchHeightPx * 2f)));
                        builder.LineTo(new SKPoint(cx + (LegendHalfSwatchHeightPx * 2f), centerY));
                        builder.LineTo(new SKPoint(cx, centerY + (LegendHalfSwatchHeightPx * 2f)));
                        builder.LineTo(new SKPoint(cx - (LegendHalfSwatchHeightPx * 2f), centerY));
                        builder.Close();

                        using var diamond = builder.Detach();
                        using var paint = new SKPaint
                        {
                            Color = _snapshot.Text,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true
                        };
                        canvas.DrawPath(diamond, paint);
                    }

                    break;
            }
        }

        /// <summary>
        /// One legend row's explanation resolved through the chart's own
        /// resource lookups (the no-data states' pattern).
        /// </summary>
        private static string GetFootprintLegendText(string key)
        {
            if (LocalizationManager.IsInitialized)
            {
                return LocalizationManager.Instance.GetString(key);
            }

            // Design mode / manager not wired yet: raw resource lookup instead.
            return Strings.ResourceManager.GetString(
                key,
                System.Globalization.CultureInfo.CurrentUICulture) ?? key;
        }

        /// <summary>
        /// The right-margin volume-at-price histogram (T-093), drawn over the
        /// candles: the visible window's spread cuts into an even bucket
        /// ladder (the pure <see cref="VolumeProfile"/> compute — exactly
        /// what the current lens shows), every bucket grows leftward from
        /// the plot's right edge by its volume's share of the heaviest
        /// bucket, and a bucket's height is exactly its price span on the
        /// shared price scale. The histogram never re-scales the axis. A
        /// window whose total volume is zero draws nothing: the localized
        /// no-volume note explains the absence instead of a fabricated shape.
        /// </summary>
        private void DrawVolumeProfile(
            SKCanvas canvas,
            Rect plotRect,
            IReadOnlyList<Bar> bars,
            decimal minAxis,
            decimal axisRange)
        {
            if (!_snapshot.VolumeProfileVisible || bars.Count == 0)
            {
                return;
            }

            var profile = VolumeProfile.Compute(bars, VolumeProfileBucketCount);
            if (profile.TotalVolume <= 0m)
            {
                return;
            }

            decimal maxVolume = 0m;
            foreach (var bucket in profile.Buckets)
            {
                if (bucket.Volume > maxVolume)
                {
                    maxVolume = bucket.Volume;
                }
            }

            if (maxVolume <= 0m)
            {
                return;
            }

            var reach = plotRect.Width * VolumeProfileMaxWidthRatio;

            // The theme text color under a translucent alpha: a neutral
            // volume body in BOTH palettes — never a candle color that
            // would claim a direction the profile does not carry.
            using var fill = new SKPaint
            {
                Color = _snapshot.Text.WithAlpha(VolumeProfileFillAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            foreach (var bucket in profile.Buckets)
            {
                if (bucket.Volume <= 0m)
                {
                    continue;
                }

                var width = (float)(bucket.Volume / maxVolume) * (float)reach;
                if (width < VolumeProfileMinBarWidthPx)
                {
                    width = VolumeProfileMinBarWidthPx;
                }

                var top = ChartPriceScale.PriceToY(plotRect, AxisOf(bucket.High), minAxis, axisRange);
                var bottom = ChartPriceScale.PriceToY(plotRect, AxisOf(bucket.Low), minAxis, axisRange);
                var height = bottom - top;
                if (height < VolumeProfileMinBarHeightPx)
                {
                    height = VolumeProfileMinBarHeightPx;
                }

                canvas.DrawRect(
                    (float)plotRect.Right - width,
                    top,
                    width,
                    height,
                    fill);
            }
        }

        /// <summary>
        /// The symbol-compare draw block (T-124): one polyline per compare
        /// layer riding the bar slots and the shared price scale (each
        /// percent layer maps through its OWN anchor — that series' own
        /// zero), then the legend chip strip pinned top-right inside the
        /// plot: one label-only chip for the main series, one
        /// swatch-and-detach chip per layer, whose × press zones (cached
        /// into the control field the pointer press reads) route the
        /// removals. An aligned gap draws nothing and never bridges to a
        /// neighbor. A no-layer frame never enters — the empty zone-cache
        /// write keeps the pointer route quiet while the identity holds.
        /// </summary>
        private void DrawCompareLayers(
            SKCanvas canvas,
            SKFont font,
            SKPaint labelPaint,
            Rect plotRect,
            double barPitch,
            int barCount,
            decimal minAxis,
            decimal axisRange)
        {
            var frames = _snapshot.Compares;

            if (frames.Count == 0)
            {
                _owner._compareChipRects = [];
                return;
            }

            using var linePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = CompareLineStrokePx,
                IsAntialias = true
            };

            foreach (var frame in frames)
            {
                using var builder = new SKPathBuilder();
                var penDown = false;
                for (var i = 0; i < barCount; i++)
                {
                    if (frame.Aligned[i] is not { } close)
                    {
                        penDown = false;
                        continue;
                    }

                    var x = (float)ChartBarLayout.BarCenterX(plotRect.Right, barPitch, barCount, i);
                    var y = ChartPriceScale.PriceToY(plotRect, AxisOfLayer(close, frame.Anchor), minAxis, axisRange);

                    if (!penDown)
                    {
                        builder.MoveTo(x, y);
                        penDown = true;
                    }
                    else
                    {
                        builder.LineTo(x, y);
                    }
                }

                // The same dispose discipline the indicator lines keep:
                // the detached path is dead right after the draw call
                // (BUG-049 — the native wrapper must not outlive it).
                using var path = builder.Detach();
                linePaint.Color = frame.Color;
                canvas.DrawPath(path, linePaint);
            }

            // The chip strip: the run's full budget measures first (the
            // two-pass idea the time-label pick keeps, at its smallest
            // scale), so the strip hangs right-anchored whole. A strip
            // wider than the plot pins to the plot's left inset and lets
            // the far end clip — a degenerate sliver frame never claims
            // mid-gutter space.
            var crossSquare = (2f * CompareChipCrossHalfPx) + (2f * CompareChipCrossPadPx);
            var textHeight = -font.Metrics.Ascent + font.Metrics.Descent;
            var chipHeight = textHeight + (2f * CompareChipPadPx);
            var chipRadius = CornerRadiusFor(chipHeight);
            var hasMainChip = !string.IsNullOrEmpty(_snapshot.MainSeriesLabel);

            var widths = new List<float>(frames.Count + 1);
            var layerWidthOffset = hasMainChip ? 1 : 0;
            var mainLabel = _snapshot.MainSeriesLabel;
            if (hasMainChip)
            {
                widths.Add((2f * CompareChipPadPx) + font.MeasureText(mainLabel));
            }

            for (var i = 0; i < frames.Count; i++)
            {
                widths.Add((2f * CompareChipPadPx)
                    + CompareChipSwatchWidthPx
                    + CompareChipSwatchTextGapPx
                    + font.MeasureText(frames[i].Label)
                    + CompareChipCrossGapPx
                    + crossSquare);
            }

            var totalWidth = widths.Sum() + ((widths.Count - 1) * CompareChipGapPx);
            var stripRight = (float)plotRect.Right - CompareStripInsetPx;
            var stripTop = (float)plotRect.Top + CompareStripInsetPx;
            var cursorX = Math.Max(stripRight - totalWidth, (float)plotRect.Left + CompareStripInsetPx);
            var baseline = stripTop + CompareChipPadPx - font.Metrics.Ascent;

            using var chipBackgroundPaint = new SKPaint
            {
                Color = _snapshot.Background.WithAlpha(CompareChipBackgroundAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var swatchPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var crossPaint = new SKPaint
            {
                Color = _snapshot.Text,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            };

            var chips = new List<CompareChipHit>(frames.Count);

            // The main series' chip comes first: label only, no detach —
            // the workspace series never removes itself from its own strip.
            if (hasMainChip)
            {
                var chipWidth = widths[0];
                canvas.DrawRoundRect(
                    new SKRect(cursorX, stripTop, cursorX + chipWidth, stripTop + chipHeight),
                    chipRadius,
                    chipRadius,
                    chipBackgroundPaint);
                canvas.DrawText(mainLabel, cursorX + CompareChipPadPx, baseline, SKTextAlign.Left, font, labelPaint);
                cursorX += chipWidth + CompareChipGapPx;
            }

            for (var i = 0; i < frames.Count; i++)
            {
                var chipWidth = widths[i + layerWidthOffset];
                canvas.DrawRoundRect(
                    new SKRect(cursorX, stripTop, cursorX + chipWidth, stripTop + chipHeight),
                    chipRadius,
                    chipRadius,
                    chipBackgroundPaint);

                // The layer's own color rides the inline swatch; the chip's
                // text and detach cross stay neutral so the labels read.
                swatchPaint.Color = frames[i].Color;
                var swatchCenterY = stripTop + (chipHeight / 2f);
                var swatchRadius = CornerRadiusFor(CompareChipSwatchHeightPx);
                canvas.DrawRoundRect(
                    new SKRect(
                        cursorX + CompareChipPadPx,
                        swatchCenterY - (CompareChipSwatchHeightPx / 2f),
                        cursorX + CompareChipPadPx + CompareChipSwatchWidthPx,
                        swatchCenterY + (CompareChipSwatchHeightPx / 2f)),
                    swatchRadius,
                    swatchRadius,
                    swatchPaint);

                var textX = cursorX + CompareChipPadPx + CompareChipSwatchWidthPx + CompareChipSwatchTextGapPx;
                canvas.DrawText(frames[i].Label, textX, baseline, SKTextAlign.Left, font, labelPaint);

                var squareLeft = cursorX + chipWidth - CompareChipPadPx - crossSquare;
                var squareRect = new SKRect(squareLeft, stripTop, cursorX + chipWidth, stripTop + chipHeight);
                var centerX = squareLeft + (squareRect.Width / 2f);
                var centerY = stripTop + (chipHeight / 2f);
                canvas.DrawLine(
                    centerX - CompareChipCrossHalfPx,
                    centerY - CompareChipCrossHalfPx,
                    centerX + CompareChipCrossHalfPx,
                    centerY + CompareChipCrossHalfPx,
                    crossPaint);
                canvas.DrawLine(
                    centerX - CompareChipCrossHalfPx,
                    centerY + CompareChipCrossHalfPx,
                    centerX + CompareChipCrossHalfPx,
                    centerY - CompareChipCrossHalfPx,
                    crossPaint);

                chips.Add(new CompareChipHit(frames[i].Label, squareRect));
                cursorX += chipWidth + CompareChipGapPx;
            }

            _owner._compareChipRects = [.. chips];
        }

        /// <summary>
        /// The honest no-volume state of the drawn overlays (T-093): the
        /// localized note prints when nothing volume-backed drew — the
        /// published VWAP overlay reports the SERIES carries no volume at
        /// all, or the toggled histogram's visible window holds none — and
        /// both triggers share this one note (the same absent fact). The
        /// candles render regardless; a single positive-volume bar calms
        /// the note, so only the honest zero-volume cases ever show it.
        /// </summary>
        private void DrawNoVolumeNote(
            SKCanvas canvas,
            SKPaint paint,
            Rect plotRect,
            IReadOnlyList<Bar> bars)
        {
            if (!ShouldDrawNoVolumeNote(bars))
            {
                return;
            }

            using var noteFont = new SKFont(SKTypeface.Default, NoVolumeNoteFontSize);
            var text = GetLocalizedNoVolumeText();
            canvas.DrawText(
                text,
                (float)plotRect.Center.X,
                (float)plotRect.Center.Y,
                SKTextAlign.Center,
                noteFont,
                paint);
        }

        /// <summary>Whether the drawn frame qualifies for the no-volume note (see <see cref="DrawNoVolumeNote"/>).</summary>
        private bool ShouldDrawNoVolumeNote(IReadOnlyList<Bar> bars)
        {
            if (_snapshot.Vwap is { State: VwapOverlayState.NoVolumeData })
            {
                return true;
            }

            if (!_snapshot.VolumeProfileVisible)
            {
                return false;
            }

            for (var i = 0; i < bars.Count; i++)
            {
                if (bars[i].Volume != 0m)
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetLocalizedNoVolumeText()
        {
            if (LocalizationManager.IsInitialized)
            {
                return LocalizationManager.Instance.GetString(NoVolumeMessageKey);
            }

            // Design mode / manager not wired yet: raw resource lookup instead.
            return Strings.ResourceManager.GetString(
                NoVolumeMessageKey,
                CultureInfo.CurrentUICulture) ?? NoVolumeMessageKey;
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
            decimal minAxis,
            decimal axisRange,
            IReadOnlyList<decimal> ticks,
            IReadOnlyList<string> texts)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                var y = ChartPriceScale.PriceToY(plotRect, ticks[i], minAxis, axisRange);
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
        /// ride the snapshot (the render thread reads no ambient state) —
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
                // bounds collapse — Max(min, max) then keeps the left bound:
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
        /// or substituting — the dead paths stay silent. The cursor's price
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
            decimal minAxis,
            decimal axisRange,
            int axisDecimals,
            int rawDecimals,
            Point? hover)
        {
            if (hover is not { } position)
            {
                return;
            }

            var x = (float)position.X;
            var y = (float)position.Y;
            var textHeight = -font.Metrics.Ascent + font.Metrics.Descent;

            // The rails: 1 px dashed lines riding the plot's inside only —
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
            // padded scale — an AXIS value — then printed per the scale
            // mode (T-090), so the pill always agrees with the axis labels
            // and the pixels under the cursor: the linear tick IS the price;
            // the log tick unwinds to its price (10^t) with the raw-price
            // decimals; the percent mode reads the relative change with its
            // own signed label.
            var axisValue = ChartPriceScale.YToPrice(position.Y, plotRect, minAxis, axisRange);
            var pillText = _snapshot.ScaleMode switch
            {
                PriceScaleMode.Logarithmic => ChartAxisFormatting.FormatPriceTick(
                    ChartPriceTransform.ToPrice(
                        axisValue,
                        PriceScaleMode.Logarithmic,
                        _snapshot.AxisAnchor,
                        _snapshot.AxisFloor),
                    rawDecimals,
                    _snapshot.Culture),
                PriceScaleMode.Percent => ChartAxisFormatting.FormatPercentTick(axisValue, axisDecimals, _snapshot.Culture),
                _ => ChartAxisFormatting.FormatPriceTick(axisValue, axisDecimals, _snapshot.Culture),
            };
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
            // print RAW values (the readout is a quote, not an axis tick —
            // T-090 keeps it mode-foreign), with the raw-price decimals and
            // the axis' culture; the letter glyphs are the universal trading
            // notation, unlocalized per the board's scope.
            var barIndex = ChartBarLayout.SlotIndexForX(plotRect.Right, barPitch, bars.Count, position.X);

            if (barIndex < 0)
            {
                return;
            }

            var bar = bars[barIndex];
            var openText = ChartAxisFormatting.FormatPriceTick(bar.Open, rawDecimals, _snapshot.Culture);
            var highText = ChartAxisFormatting.FormatPriceTick(bar.High, rawDecimals, _snapshot.Culture);
            var lowText = ChartAxisFormatting.FormatPriceTick(bar.Low, rawDecimals, _snapshot.Culture);
            var closeText = ChartAxisFormatting.FormatPriceTick(bar.Close, rawDecimals, _snapshot.Culture);
            var volumeText = ChartAxisFormatting.FormatPriceTick(bar.Volume, rawDecimals, _snapshot.Culture);
            var readout = $"O {openText}  H {highText}  L {lowText}  C {closeText}  V {volumeText}";
            var readoutBaseline = (float)plotRect.Top + CrosshairReadoutPadPx - font.Metrics.Ascent;

            canvas.DrawText(readout, (float)plotRect.Left + CrosshairReadoutPadPx, readoutBaseline, SKTextAlign.Left, font, textPaint);
        }
    }

    #endregion

    #region Snapshot Export (T-094)

    /// <summary>
    /// Render the current frame — the exact snapshot the screen paints, same
    /// lens and annotations state (the acceptance) — into a PNG file: the
    /// frame draws at a fixed supersampling scale through the SAME
    /// <see cref="CandlestickDrawOperation"/> the on-screen render runs, and
    /// a translucent watermark stamps the context (symbol, time frame) over
    /// the bottom-right corner. The caller supplies the destination (the
    /// save dialog is view behavior); a render or write fault throws — the
    /// chart panel's export flow surfaces it on its error line.
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="watermark">The context line stamped over the frame, or
    /// null to skip it (the seeded export facts' differ probe).</param>
    /// <param name="cancellationToken">Cancels the file write; the render
    /// itself is synchronous and brief.</param>
    public async Task ExportSnapshotPngAsync(
        string path,
        string? watermark,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The snapshot export needs a destination path.", nameof(path));
        }

        var payload = RenderSnapshotPngBytes(watermark);
        await File.WriteAllBytesAsync(path, payload, cancellationToken);
    }

    /// <summary>
    /// Render the current frame into PNG bytes — the snapshot export's
    /// payload (the file write stays in the public method): the frame draws
    /// at a fixed supersampling scale through the SAME
    /// <see cref="CandlestickDrawOperation"/> the on-screen render runs, and
    /// a translucent watermark stamps the context (symbol, time frame) over
    /// the bottom-right corner. Runs on the UI thread (the control's state
    /// is affinity-bound); a render fault throws to the caller.
    /// </summary>
    /// <param name="watermark">The context line stamped over the frame, or
    /// null to skip it (the seeded export facts' differ probe).</param>
    /// <returns>The PNG-encoded frame.</returns>
    internal byte[] RenderSnapshotPngBytes(string? watermark)
    {
        // The frame must have a size: an export before any layout pass would
        // render an empty rect (the real window always has one by the time
        // the toolbar is clickable; the seeded facts arrange the control).
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));

        // The raster surface (no GPU): the frame renders off the SAME draw
        // operation the screen uses, so "the export matches the on-screen
        // frame" holds by construction — one renderer, one snapshot.
        using var surface = SKSurface.Create(
            new SKImageInfo(width * ExportScale, height * ExportScale, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
        {
            throw new InvalidOperationException("The chart snapshot export could not create its raster surface.");
        }

        using var operation = new CandlestickDrawOperation(new Rect(0, 0, width, height), CreateSnapshot(), this);
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.Scale(ExportScale);
        try
        {
            operation.Draw(canvas);
            if (watermark is not null)
            {
                DrawWatermark(canvas, width, height, watermark);
            }
        }
        finally
        {
            canvas.Restore();
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, PngEncodeQuality);

        return encoded.ToArray();
    }

    /// <summary>The watermark: the context line over the frame's bottom-right corner, translucent so it never buries the content.</summary>
    private void DrawWatermark(
        SKCanvas canvas,
        double width,
        double height,
        string watermark)
    {
        using var font = new SKFont(SKTypeface.Default, WatermarkFontSize);
        using var paint = new SKPaint
        {
            Color = _textColor.WithAlpha(WatermarkAlpha),
            IsAntialias = true,
        };

        var textWidth = font.MeasureText(watermark);
        canvas.DrawText(
            watermark,
            (float)width - textWidth - WatermarkInsetPx,
            (float)height - WatermarkInsetPx,
            SKTextAlign.Left,
            font,
            paint);
    }

    #endregion Snapshot Export (T-094)

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
    /// A press inside a compare chip's cached detach zone (T-124) routes
    /// the removal instead and stops there — the handled event never arms
    /// a pan drag for what was a chip press.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            var position = e.GetPosition(this);

            // The compare chips' detach squares (T-124): the render thread
            // owns the cache; the worst this read sees is the previous
            // frame's zones (the painted-bar-pitch discipline). The hit
            // emits the chip's own label and marks itself handled — the
            // pan capture below stays out of the route entirely.
            foreach (var chip in _compareChipRects)
            {
                if (chip.Bounds.Contains((float)position.X, (float)position.Y))
                {
                    _compareRemovals.OnNext(chip.Label);
                    e.Handled = true;
                    Focus();
                    return;
                }
            }

            _panAnchorX = position.X;
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
    /// integer slots — the pitch is the painted one the draw pass last
    /// charted with, so the scroll distance matches the bar width).
    /// </summary>
    private void ApplyPan(double draggedX)
    {
        var bars = Bars;
        if (_panAnchorX is not { } anchorX || bars is not { Count: > 0 })
        {
            return;
        }

        var barCount = bars.Count;

        // The pan step paces the slots the draw pass PAINTS per pixel: the
        // pitch is the one the last draw pass computed from the plot width
        // (bounds minus the measured price gutter) and cached — the old
        // re-derivation from the RAW window size dragged "one bar" by a
        // different distance than the painted bar width (BUG-048).
        var pitch = _paintedBarPitch;
        if (pitch <= 0)
        {
            return;
        }

        // Dragging RIGHT slides the timeline right: older bars enter the
        // plot from the left, the offset grows. The distance is taken from
        // the drag's own anchor (not per-event deltas) so a wave of move
        // events accumulates without rounding drift.
        var slots = ChartPanWindow.SlotsForDrag(draggedX - anchorX, pitch);
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
