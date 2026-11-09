namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// The market heatmap panel state (T-121): the ranked tile page over the
/// feed's coalesced snapshots. The feed owns the candidate set, the cadence
/// and the ranking; the ViewModel only surfaces the page to the widget (one
/// immutable list per pulse), the metric selection (the index shim the
/// window's combo binds — the localized labels never touch the ViewModel),
/// and the tile-press route into the workspace symbol.
/// </summary>
/// <remarks>
/// The panel ViewModel lives as long as the application (a workspace
/// singleton), so its subscriptions mirror that lifetime and need no
/// explicit disposal, exactly like every workspace panel's ViewModel.
/// </remarks>
public sealed class HeatmapViewModel : ReactiveObject
{
    #region Private Fields

    /// <summary>The feed whose ranked snapshots and metric choice surface here.</summary>
    private readonly HeatmapFeedService _feed;

    /// <summary>The chart ViewModel whose <c>SelectedSymbol</c> IS the workspace symbol context.</summary>
    private readonly ChartViewModel _chart;

    /// <summary>The panel's own logging lane (route refusals land here).</summary>
    private readonly ILogger<HeatmapViewModel> _logger;

    #endregion Private Fields

    #region Properties

    private IReadOnlyList<HeatmapTile> _tiles = [];

    /// <summary>
    /// The ranked tiles the widget renders (the feed's latest snapshot, the
    /// rank order as the feed ranked it). An empty page renders the widget's
    /// honest empty state.
    /// </summary>
    public IReadOnlyList<HeatmapTile> Tiles
    {
        get => _tiles;
        private set => this.RaiseAndSetIfChanged(ref _tiles, value);
    }

    private bool _hasData;

    /// <summary>
    /// Whether the feed's page carries ranked pairs: the metric switch shows
    /// only when data exists (a switch with nothing to size is fabricated
    /// chrome over a missing feed).
    /// </summary>
    public bool HasData
    {
        get => _hasData;
        private set => this.RaiseAndSetIfChanged(ref _hasData, value);
    }

    /// <summary>
    /// The metric switch's index shim: 0 binds the quote-volume metric, 1 the
    /// 24h change (the combo's static localized items bind by index, the
    /// chart type selector's precedent). The feed re-plans the ranking on its
    /// next pulse — a flip mid-storm rides the same coalesced cadence.
    /// </summary>
    public int SelectedMetricIndex
    {
        get => _feed.Metric == HeatmapMetric.Change24hPercent ? 1 : 0;
        set => _feed.Metric = value == 1
            ? HeatmapMetric.Change24hPercent
            : HeatmapMetric.QuoteVolume24h;
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the panel over the feed: the snapshots flow through the UI
    /// thread so the page writes stay on it (this VM lives as long as the
    /// application, so this subscription mirrors it).
    /// </summary>
    public HeatmapViewModel(
        HeatmapFeedService feed,
        ChartViewModel chart,
        ILogger<HeatmapViewModel> logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        feed.Snapshots
            .ObserveOnUiThread()
            .Subscribe(
                ApplySnapshot,
                ex => logger.LogError(ex, "The heatmap snapshot stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Snapshot Apply

    /// <summary>
    /// Apply one ranked page: the widget's tile list and the empty-state
    /// flag move together (a page and its emptiness are one panel state —
    /// never a switch half-applied).
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<HeatmapTile> snapshot)
    {
        Tiles = snapshot;
        HasData = snapshot.Count > 0;
    }

    #endregion Snapshot Apply

    #region Tile Press Route

    /// <summary>
    /// Route one pressed tile: the tile's symbol becomes the workspace
    /// symbol context (the same write the palette's same-exchange apply
    /// maintains). An unparsable text is refused honestly — the press
    /// unrouts and the refusal logs; a substitute symbol is never invented
    /// for a bad tile text.
    /// </summary>
    /// <param name="click">The pressed tile's symbol text.</param>
    /// <returns>The route's completed task.</returns>
    public Task RouteTileAsync(HeatmapTileClick click)
    {
        var resolved = ResolveTileSymbol(click.SymbolText);
        if (resolved is null)
        {
            _logger.LogWarning(
                "Heatmap: tile text '{SymbolText}' does not parse on the active exchange; the press stays unrouted",
                click.SymbolText);
            return Task.CompletedTask;
        }

        _chart.SelectedSymbol = resolved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve a pressed tile's text to the symbol it names (pure: the
    /// workspace route's predicate). Null means refusal — an empty text or
    /// one the active exchange's parser never accepts owns no route, and
    /// nothing substitutes for it.
    /// </summary>
    /// <param name="symbolText">The pressed tile's symbol text.</param>
    /// <returns>The parsed symbol, or null when the text refuses.</returns>
    internal static Symbol? ResolveTileSymbol(string? symbolText)
    {
        if (string.IsNullOrWhiteSpace(symbolText))
        {
            return null;
        }

        try
        {
            return Symbol.Parse(symbolText);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    #endregion Tile Press Route
}
