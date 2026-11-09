namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
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
using ZettaTrade.UI.Resources;

/// <summary>
/// Market heatmap tile field drawn straight onto Avalonia's Skia canvas via
/// <see cref="ICustomDrawOperation"/> (the order-book widget pattern: no
/// intermediate bitmaps, no per-tile controls). The snapshot arrives through
/// <see cref="Tiles"/> in the feed's rank order; the widget plans the pure
/// squarified treemap (<see cref="HeatmapTileLayout"/>), colors every tile
/// by its 24h change (<see cref="HeatmapChangePalette"/>) and captions it
/// with the symbol and its signed change. A pointer press hit-tests against
/// the exact rects the current frame drew and publishes the pressed tile's
/// symbol text — the routing decision (the workspace symbol write) belongs
/// to the panel, never the widget.
/// </summary>
public class HeatmapWidget : Control
{
    #region Constants

    /// <summary>Top and bottom inset the treemap keeps from the widget bounds.</summary>
    private const float PlotInsetPx = 1;

    /// <summary>Symbol caption size the largest tiles grow to.</summary>
    private const float CaptionFontSizeMaxPx = 13;

    /// <summary>Symbol caption size under which a caption stops (no cramped ink).</summary>
    private const float CaptionFontSizeMinPx = 7;

    /// <summary>How fast captions shrink across the fit loop.</summary>
    private const float CaptionFontSizeStepPx = 0.5f;

    /// <summary>The widget takes a snapshot of these resources on each redraw.</summary>
    private const string NoDataMessageKey = "Heatmap.NoDataMessage";

    /// <summary>Resource keys published by ThemeManager; the fill scale reuses
    /// the candles' own gain/loss pair and the caption rides the text color,
    /// mirrors the book/tape widgets.</summary>
    private const string BackgroundColorKey = "Background";
    private const string NeutralColorKey = "Border";
    private const string TextColorKey = "TextPrimary";
    private const string UpColorKey = "CandleUp";
    private const string DownColorKey = "CandleDown";

    #endregion

    #region Styled Properties

    /// <summary>StyledProperty for the current ranked tile snapshot.</summary>
    public static readonly StyledProperty<IReadOnlyList<HeatmapTile>?> TilesProperty =
        AvaloniaProperty.Register<HeatmapWidget, IReadOnlyList<HeatmapTile>?>(nameof(Tiles));

    /// <summary>
    /// Gets or sets the ranked tiles to render; null or empty renders the
    /// no-data state. The input order is the rank order — the widget never
    /// re-sorts behind the feed's back.
    /// </summary>
    public IReadOnlyList<HeatmapTile>? Tiles
    {
        get => GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    #endregion

    #region Fields & Properties

    // Theme-agnostic fallback colors: the built-in dark set, named so the
    // literals stay out of inline code; the live colors are refreshed from
    // the application resources on every snapshot (the chart's convention).
    private static readonly SKColor DefaultBackgroundColor = new(20, 20, 30);
    private static readonly SKColor DefaultNeutralColor = new(45, 45, 60);
    private static readonly SKColor DefaultTextColor = new(200, 200, 200);
    private static readonly SKColor DefaultUpColor = new(34, 197, 94);
    private static readonly SKColor DefaultDownColor = new(239, 68, 68);

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private SKColor _neutralColor = DefaultNeutralColor;
    private SKColor _textColor = DefaultTextColor;
    private SKColor _upColor = DefaultUpColor;
    private SKColor _downColor = DefaultDownColor;

    /// <summary>Subscription of the theme-changed stream; disposed on detach.</summary>
    private IDisposable? _themeChangeSubscription;

    /// <summary>Subscription of the language-changed stream; disposed on detach.</summary>
    private IDisposable? _languageChangeSubscription;

    /// <summary>
    /// The last frame's laid tiles (the colors and bounds the current frame
    /// drew): the pointer hit-test resolves against exactly this list, so a
    /// press lands on what the eye sees or nowhere at all.
    /// </summary>
    private IReadOnlyList<HeatmapDrawnTile> _lastFrame = [];

    /// <summary>The tile-presses stream sink (hot; see <see cref="TileClicked"/>).</summary>
    private readonly Subject<HeatmapTileClick> _tileClicked = new();

    /// <summary>
    /// The tile presses stream, hot and fed on the UI thread: a pointer press
    /// on a drawn tile publishes just the tile's own symbol text. The route
    /// (the workspace symbol write, or the honest refusal of an unparsable
    /// text) lives in the panel's ViewModel.
    /// </summary>
    public IObservable<HeatmapTileClick> TileClicked => _tileClicked;

    #endregion

    #region Constructor

    static HeatmapWidget()
    {
        TilesProperty.Changed.AddClassHandler<HeatmapWidget>((x, _) =>
            Dispatcher.UIThread.Post(x.InvalidateVisual));
    }

    public HeatmapWidget()
    {
        // A plain Avalonia Control is hit-tested only through its children:
        // the drawn surface (custom draw operation) stays input-invisible.
        // A transparent child rectangle gives the whole bounds a hit-test
        // surface, so tile presses reach OnPointerPressed; events bubble
        // from the child to this widget.
        var hitSurface = new Rectangle { Fill = Brushes.Transparent };
        hitSurface.Bind(
            Rectangle.WidthProperty,
            this.GetObservable(BoundsProperty).Select(b => b.Width));
        hitSurface.Bind(Rectangle.HeightProperty, this.GetObservable(BoundsProperty).Select(b => b.Height));
        VisualChildren.Add(hitSurface);

        ClipToBounds = true;
    }

    #endregion

    #region Theme & Language Tracking

    /// <summary>
    /// While attached, re-render on every theme or language change (the
    /// chart's convention): the draw operation takes a snapshot per render —
    /// RefreshThemeColors re-reads the resources and the captions re-resolve
    /// the localized no-data message — so invalidating is all it takes to
    /// pick up new colors and text. Without this a theme or language switch
    /// left the drawn field stale until the next tile snapshot arrived.
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
        // an immutable snapshot, never the live UI-thread tile list.
        var snapshot = CreateSnapshot();
        _lastFrame = snapshot.Captions;
        context.Custom(new HeatmapDrawOperation(new Rect(size), snapshot));
    }

    /// <summary>
    /// Plan the treemap, resolve the palette colors and format the captions
    /// so the render thread paints plain precomputed shapes and strings.
    /// </summary>
    private HeatmapSnapshot CreateSnapshot()
    {
        RefreshThemeColors();

        var tiles = Tiles;
        var bounds = new Rect(
            PlotInsetPx,
            PlotInsetPx,
            Math.Max(0, Bounds.Size.Width - (2 * PlotInsetPx)),
            Math.Max(0, Bounds.Size.Height - (2 * PlotInsetPx)));
        IReadOnlyList<HeatmapDrawnTile> drawn = [];
        if (tiles is { Count: > 0 } ranked)
        {
            var laid = HeatmapTileLayout.Layout(ranked, bounds);
            drawn = BuildCaptions(laid);
        }

        return new HeatmapSnapshot(
            drawn,
            _backgroundColor,
            _neutralColor,
            _textColor,
            _upColor,
            _downColor,
            GetLocalizedNoDataText());
    }

    /// <summary>
    /// One laid tile's full draw state: the palette fill resolved on the UI
    /// thread, the symbol caption and (when the tile carries a change) the
    /// signed percent text, both prebuilt in the snapshot's own culture so
    /// the render thread never touches the resource manager.
    /// </summary>
    private sealed record HeatmapDrawnTile(
        string Symbol,
        string? ChangeText,
        SKColor Fill,
        Rect Bounds);

    private IReadOnlyList<HeatmapDrawnTile> BuildCaptions(IReadOnlyList<HeatmapTileRect> laid)
    {
        var culture = HeatmapTileCaptions.Culture();
        var captions = new List<HeatmapDrawnTile>(laid.Count);
        foreach (var tile in laid)
        {
            captions.Add(new HeatmapDrawnTile(
                tile.SymbolText,
                tile.Change24hPercent is { } change
                    ? HeatmapTileCaptions.FormatChangePercent(change, culture)
                    : null,
                HeatmapChangePalette.ColorFor(
                    tile.Change24hPercent,
                    _neutralColor,
                    _upColor,
                    _downColor),
                tile.Bounds));
        }

        return captions;
    }

    private sealed record HeatmapSnapshot(
        IReadOnlyList<HeatmapDrawnTile> Captions,
        SKColor Background,
        SKColor Neutral,
        SKColor Text,
        SKColor Up,
        SKColor Down,
        string NoDataText);

    /// <summary>
    /// Refresh the draw colors from the theme resources published by ThemeManager
    /// (Application.Current.Resources); falls back to the dark-theme defaults.
    /// </summary>
    private void RefreshThemeColors()
    {
        _backgroundColor = ResourceColor(BackgroundColorKey, _backgroundColor);
        _neutralColor = ResourceColor(NeutralColorKey, _neutralColor);
        _textColor = ResourceColor(TextColorKey, _textColor);
        _upColor = ResourceColor(UpColorKey, _upColor);
        _downColor = ResourceColor(DownColorKey, _downColor);
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

        // The tile under the cursor resolves against the last frame's laid
        // rects — the same list the frame drew. A press off every drawn tile
        // (or onto the empty state) stays unrouted.
        var position = e.GetPosition(this);
        foreach (var tile in _lastFrame)
        {
            if (tile.Bounds.Contains(position))
            {
                _tileClicked.OnNext(new HeatmapTileClick(tile.Symbol));
                break;
            }
        }
    }

    #endregion

    /// <summary>
    /// Draws the planned tile field straight onto Avalonia's own SKCanvas (render thread).
    /// </summary>
    private sealed class HeatmapDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly HeatmapSnapshot _snapshot;

        public HeatmapDrawOperation(Rect bounds, HeatmapSnapshot snapshot)
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

            if (_snapshot.Captions.Count == 0)
            {
                DrawNoDataMessage(canvas, width, height);
                return;
            }

            DrawTiles(canvas);
        }

        private void DrawTiles(SKCanvas canvas)
        {
            using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
            using var separatorPaint = new SKPaint
            {
                Color = _snapshot.Background,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = false,
            };
            using var textPaint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };
            using var font = new SKFont(SKTypeface.Default, CaptionFontSizeMaxPx);

            foreach (var tile in _snapshot.Captions)
            {
                var rect = SKRect.Create(
                    (float)tile.Bounds.X,
                    (float)tile.Bounds.Y,
                    (float)tile.Bounds.Width,
                    (float)tile.Bounds.Height);

                fillPaint.Color = tile.Fill;
                canvas.DrawRect(rect, fillPaint);
                canvas.DrawRect(rect, separatorPaint);

                DrawCaptions(canvas, tile, rect, font, textPaint);
            }
        }

        /// <summary>
        /// The tile's captions: the symbol first, the signed change under it
        /// when the tile carries one. The font size shrinks until the symbol
        /// fits its band; a tile that cannot fit even the minimum size stays
        /// uncaptioned (the rank's area carries it — no cramped ink, and the
        /// text is never truncated to fake a fit).
        /// </summary>
        private void DrawCaptions(
            SKCanvas canvas,
            HeatmapDrawnTile tile,
            SKRect rect,
            SKFont font,
            SKPaint textPaint)
        {
            var innerWidth = rect.Width - 2;
            var innerBand = rect.Height - 2;
            if (innerWidth < 1 || innerBand < 1)
            {
                return;
            }

            for (var size = CaptionFontSizeMaxPx; size >= CaptionFontSizeMinPx; size -= CaptionFontSizeStepPx)
            {
                font.Size = size;
                if ((font.MeasureText(tile.Symbol, textPaint) / 2f) > innerWidth)
                {
                    continue;
                }

                // Two lines demand the doubled band; one line keeps the tile honest.
                if (tile.ChangeText is { } change &&
                    (2 * size) <= innerBand)
                {
                    var symbolY = rect.MidY - (size * 0.18f);
                    var changeY = symbolY + (size * 1.18f);
                    DrawCentered(canvas, tile.Symbol, rect.MidX, symbolY, font, textPaint);
                    DrawCentered(canvas, change, rect.MidX, changeY, font, textPaint);
                }
                else if (size <= innerBand)
                {
                    DrawCentered(canvas, tile.Symbol, rect.MidX, rect.MidY, font, textPaint);
                }

                return;
            }
        }

        private static void DrawCentered(
            SKCanvas canvas,
            string text,
            float centerX,
            float baselineY,
            SKFont font,
            SKPaint paint) =>
            canvas.DrawText(text, centerX, baselineY, SKTextAlign.Center, font, paint);

        private void DrawNoDataMessage(SKCanvas canvas, float width, float height)
        {
            using var paint = new SKPaint
            {
                Color = _snapshot.Text,
                IsAntialias = true,
            };

            using var font = new SKFont(SKTypeface.Default, 12);

            canvas.DrawText(
                _snapshot.NoDataText,
                width / 2,
                height / 2,
                SKTextAlign.Center,
                font,
                paint);
        }
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
