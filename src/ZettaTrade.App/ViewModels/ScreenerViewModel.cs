namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The market screener panel state (T-195): the sortable table page over
/// the feed's coalesced snapshots. The feed owns the candidate set, the
/// cadence and the adapter-order rows; the ViewModel owns the ranking —
/// the preset views (all / top gainers / top losers / top volume) and the
/// manual column sorts — and the row-press route into the workspace
/// symbol. A preset or sort flip rebuilds the rows IMMEDIATELY from the
/// held page (sorting is a free local re-plan, never a feed re-arm).
/// </summary>
/// <remarks>
/// The panel ViewModel lives as long as the application (a workspace
/// singleton), so its subscriptions mirror that lifetime and need no
/// explicit disposal, exactly like every workspace panel's ViewModel.
/// </remarks>
public sealed class ScreenerViewModel : ReactiveObject
{
    #region Private Fields

    /// <summary>The feed whose coalesced pages surface here.</summary>
    private readonly ScreenerFeedService _feed;

    /// <summary>The chart ViewModel whose <c>SelectedSymbol</c> IS the workspace symbol context.</summary>
    private readonly ChartViewModel _chart;

    /// <summary>The panel's own logging lane (route refusals land here).</summary>
    private readonly ILogger<ScreenerViewModel> _logger;

    /// <summary>The last applied page: the presets and sorts re-plan over it locally.</summary>
    private ScreenerSnapshot? _lastSnapshot;

    private IReadOnlyList<ScreenerRowViewModel> _rows = [];

    private bool _hasRows;

    private bool _isLive;

    private bool _isWaitingTicks;

    private bool _isCatalogUnavailable = true;

    private int _selectedPresetIndex;

    /// <summary>The standing sort (None = the adapter's own order), shared by the presets and the manual column clicks.</summary>
    private ScreenerSortColumn _sortColumn = ScreenerSortColumn.None;

    private bool _sortDescending;

    #endregion Private Fields

    #region Commands

    /// <summary>Sort by the last price column (a repeat press flips the direction).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SortByPriceCommand { get; }

    /// <summary>Sort by the 24h change column (a repeat press flips the direction).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SortByChangeCommand { get; }

    /// <summary>Sort by the 24h volume column (a repeat press flips the direction).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SortByVolumeCommand { get; }

    /// <summary>Route one pressed row's symbol into the workspace (the row template binds it).</summary>
    public ReactiveCommand<ScreenerRowViewModel, RxVoid> RouteRowCommand { get; }

    #endregion Commands

    #region Properties

    /// <summary>
    /// The display rows the table renders: the standing preset filter and
    /// sort over the feed's last live page. An empty list renders the
    /// honest empty state (the reason line or the waiting line carries the
    /// why when the feed is not live).
    /// </summary>
    public IReadOnlyList<ScreenerRowViewModel> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    /// <summary>Whether the table carries rows right now.</summary>
    public bool HasRows
    {
        get => _hasRows;
        private set => this.RaiseAndSetIfChanged(ref _hasRows, value);
    }

    /// <summary>
    /// Whether the feed's page is live: the preset picker and the sortable
    /// headers show only over live data (a switch over a missing feed is
    /// fabricated chrome — the heatmap's precedent).
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        private set => this.RaiseAndSetIfChanged(ref _isLive, value);
    }

    /// <summary>Whether the feed is armed but unpriced: the waiting line replaces the table.</summary>
    public bool IsWaitingTicks
    {
        get => _isWaitingTicks;
        private set => this.RaiseAndSetIfChanged(ref _isWaitingTicks, value);
    }

    /// <summary>Whether the catalog is absent or failed: the reason line replaces the table.</summary>
    public bool IsCatalogUnavailable
    {
        get => _isCatalogUnavailable;
        private set => this.RaiseAndSetIfChanged(ref _isCatalogUnavailable, value);
    }

    /// <summary>
    /// The preset view's index shim: 0 all, 1 top gainers, 2 top losers, 3
    /// top volume (the combo's static localized items bind by index, the
    /// chart type selector's precedent). A manual column sort retires the
    /// preset back to All.
    /// </summary>
    public int SelectedPresetIndex
    {
        get => _selectedPresetIndex;
        set
        {
            if (_selectedPresetIndex == value)
            {
                return;
            }

            _selectedPresetIndex = value;
            this.RaisePropertyChanged(nameof(SelectedPresetIndex));
            ApplyPreset(value);
        }
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the panel over the feed: the pages flow through the UI thread
    /// so the row writes stay on it (this VM lives as long as the
    /// application, so this subscription mirrors it).
    /// </summary>
    public ScreenerViewModel(
        ScreenerFeedService feed,
        ChartViewModel chart,
        ILogger<ScreenerViewModel> logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        feed.Snapshots
            .ObserveOnUiThread()
            .Subscribe(
                ApplySnapshot,
                ex => logger.LogError(ex, "The screener snapshot stream stopped unexpectedly"));

        SortByPriceCommand = ReactiveCommand.Create(() => SortBy(ScreenerSortColumn.Price));
        SortByChangeCommand = ReactiveCommand.Create(() => SortBy(ScreenerSortColumn.Change));
        SortByVolumeCommand = ReactiveCommand.Create(() => SortBy(ScreenerSortColumn.Volume));
        RouteRowCommand = ReactiveCommand.CreateFromTask<ScreenerRowViewModel, RxVoid>(RouteRowAsync);
    }

    #endregion Constructor

    #region Snapshot Apply

    /// <summary>
    /// Apply one page: the state flags, the held page and the display rows
    /// move together (a page, its state and its table are one panel state —
    /// never a table half-applied over a stale reason line).
    /// </summary>
    public void ApplySnapshot(ScreenerSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        IsLive = snapshot.State == ScreenerFeedState.Live;
        IsWaitingTicks = snapshot.State == ScreenerFeedState.NoTicksYet;
        IsCatalogUnavailable = snapshot.State == ScreenerFeedState.CatalogUnavailable;
        RebuildRows();
    }

    /// <summary>
    /// Rebuild the display rows from the held page under the standing
    /// preset and sort — the preset/sort flips re-plan locally (sorting is
    /// free), the feed's next page lands the same way. A non-live page
    /// empties the table: the reason line carries the why.
    /// </summary>
    private void RebuildRows()
    {
        if (_lastSnapshot is not { } page || page.State != ScreenerFeedState.Live)
        {
            Rows = [];
            HasRows = false;
            return;
        }

        IEnumerable<ScreenerRow> rows = page.Rows;

        // The preset's filter, then the standing sort (the preset itself
        // wrote it: gainers ride the descending change, losers the
        // ascending one, top volume the descending volume).
        rows = _selectedPresetIndex switch
        {
            1 => rows.Where(static row => row.Change24hPercent > 0m),
            2 => rows.Where(static row => row.Change24hPercent < 0m),
            _ => rows,
        };

        rows = _sortColumn switch
        {
            ScreenerSortColumn.Price => OrderBy(rows, static row => row.LastPrice),
            ScreenerSortColumn.Change => OrderBy(rows, static row => row.Change24hPercent),
            ScreenerSortColumn.Volume => OrderBy(rows, static row => row.Volume24h),
            _ => rows,
        };

        var display = rows.Select(static row => FormatRow(row)).ToList();
        Rows = display;
        HasRows = display.Count > 0;
    }

    /// <summary>
    /// Order the rows by the standing direction, ties resolving by the
    /// ordinal symbol text — a re-plan stays deterministic whatever the
    /// map's internal order did.
    /// </summary>
    private IOrderedEnumerable<ScreenerRow> OrderBy<TKey>(
        IEnumerable<ScreenerRow> rows,
        Func<ScreenerRow, TKey> key) =>
        _sortDescending
            ? rows.OrderByDescending(key).ThenBy(static row => row.SymbolText, StringComparer.Ordinal)
            : rows.OrderBy(key).ThenBy(static row => row.SymbolText, StringComparer.Ordinal);

    /// <summary>Format one feed row into the display row (the shared watchlist helpers).</summary>
    private static ScreenerRowViewModel FormatRow(ScreenerRow row) => new()
    {
        Symbol = row.SymbolText,
        Price = WatchlistRowPresentation.FormatPrice(row.LastPrice, CultureInfo.CurrentCulture),
        Change = WatchlistRowPresentation.FormatSignedPercent(row.Change24hPercent, CultureInfo.CurrentCulture),
        Volume = WatchlistRowPresentation.FormatVolume(row.Volume24h, CultureInfo.CurrentCulture),
        IsChangeUp = row.Change24hPercent > 0m,
        IsChangeDown = row.Change24hPercent < 0m,
    };

    #endregion Snapshot Apply

    #region Presets and Sorts

    /// <summary>
    /// Apply the preset view: each preset writes its own filter plus sort
    /// (gainers the descending positive change, losers the ascending
    /// negative one, top volume the descending volume) and rebuilds the
    /// rows immediately from the held page.
    /// </summary>
    private void ApplyPreset(int index)
    {
        switch (index)
        {
            case 1:
                _sortColumn = ScreenerSortColumn.Change;
                _sortDescending = true;
                break;
            case 2:
                _sortColumn = ScreenerSortColumn.Change;
                _sortDescending = false;
                break;
            case 3:
                _sortColumn = ScreenerSortColumn.Volume;
                _sortDescending = true;
                break;
            default:
                _sortColumn = ScreenerSortColumn.None;
                _sortDescending = false;
                break;
        }

        RebuildRows();
    }

    /// <summary>
    /// One column header's press: the standing column flips direction, a
    /// new column takes the descending default, and the preset retires to
    /// All (a manual sort owns the whole table). The combo mirrors the
    /// reset without re-applying it — the sort is already computed.
    /// </summary>
    private void SortBy(ScreenerSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = true;
        }

        _selectedPresetIndex = 0;
        this.RaisePropertyChanged(nameof(SelectedPresetIndex));
        RebuildRows();
    }

    /// <summary>The sortable column keys (None = the adapter's own order).</summary>
    public enum ScreenerSortColumn
    {
        /// <summary>The adapter's own catalog order.</summary>
        None,

        /// <summary>The ticker's last price.</summary>
        Price,

        /// <summary>The ticker's 24h change percent.</summary>
        Change,

        /// <summary>The ticker's 24h volume.</summary>
        Volume,
    }

    #endregion Presets and Sorts

    #region Row Press Route

    /// <summary>
    /// Route one pressed row: the row's symbol becomes the workspace symbol
    /// context (the same write the palette's same-exchange apply and the
    /// heatmap's tile route maintain). An unparsable text is refused
    /// honestly — the press unrouts and the refusal logs; a substitute
    /// symbol is never invented for a bad row text.
    /// </summary>
    /// <param name="row">The pressed row.</param>
    /// <returns>The route's completed signal.</returns>
    public async Task<RxVoid> RouteRowAsync(ScreenerRowViewModel row)
    {
        var resolved = ResolveRowSymbol(row.Symbol);
        if (resolved is null)
        {
            _logger.LogWarning(
                "Screener: row text '{SymbolText}' does not parse on the active exchange; the press stays unrouted",
                row.Symbol);
            return RxVoid.Default;
        }

        _chart.SelectedSymbol = resolved;
        return RxVoid.Default;
    }

    /// <summary>
    /// Resolve a pressed row's text to the symbol it names (pure: the
    /// workspace route's predicate). Null means refusal — an empty text or
    /// one the active exchange's parser never accepts owns no route, and
    /// nothing substitutes for it.
    /// </summary>
    /// <param name="symbolText">The pressed row's symbol text.</param>
    /// <returns>The parsed symbol, or null when the text refuses.</returns>
    internal static Symbol? ResolveRowSymbol(string? symbolText)
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

    #endregion Row Press Route
}
