namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Trade statistics window view model (modal). Pages the journal through
/// <see cref="IOrderStatsQuery"/> — one bounded keyset page per step — and
/// refines the aggregate after every page, so six-figure histories stay
/// responsive from the first page on (no first-load wall). The filters
/// (symbol, exchange, date range, paper-live tag) restart the scan; a scan
/// abandons itself quietly when a newer one takes over. Display values are
/// precomputed invariant-culture strings, exactly like the other panels'
/// rows; the equity points bind the Skia chart. The realized-trades table
/// (T-211) lists every booked close with its journal annotations and a
/// per-row edit affordance. The tag lane (T-211) is one more filter: Apply
/// restarts the scan with it and the accumulator gates OUTCOME booking on
/// it — the lane filters trades, never the scope itself (the volume and
/// the pairing state stay whole; disclosed on the accumulator). An applied
/// annotation edit re-evaluates the lane only on the next Apply: the lane
/// is a scan scope, not a live filter. The export routes (T-210) ride the
/// window's data sources: the traded surface's positions as one CSV, and
/// the whole statement bundle (orders + fills + the fill-pair realized
/// ledger + a balances snapshot, CSVs plus a JSON manifest) under the
/// window's current filter — the order-side sections stream into their
/// files page by page during the scan (T-219), and an empty range writes
/// nothing, never a broken file. The statement is the raw record: its own
/// fresh scan never gates on the tag lane, so the ledger rides every trade
/// the filter scope saw.
/// </summary>
public sealed class TradeStatsViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "Stats.LoadFailed";
    private const string InvalidSymbolKey = "Stats.InvalidSymbol";
    private const string ScanningKey = "Stats.Scanning";
    private const string ScannedKey = "Stats.Scanned";
    private const string ExportFailedKey = "Stats.ExportFailedFormat";
    private const string StatementEmptyKey = "Stats.StatementEmptyRange";
    private const string EditFailedKey = "Stats.EditFailedFormat";
    private const string ExcursionStateKey = "Stats.ExcursionState";
    private const string WeekdayMonKey = "Stats.WeekdayMon";
    private const string WeekdayTueKey = "Stats.WeekdayTue";
    private const string WeekdayWedKey = "Stats.WeekdayWed";
    private const string WeekdayThuKey = "Stats.WeekdayThu";
    private const string WeekdayFriKey = "Stats.WeekdayFri";
    private const string WeekdaySatKey = "Stats.WeekdaySat";
    private const string WeekdaySunKey = "Stats.WeekdaySun";

    #endregion Resx Keys

    #region Private Constants

    /// <summary>Journal rows per page read; the store clamps to a bounded window.</summary>
    private const int PageSize = 2000;

    /// <summary>ComboBox index of the "all venues" placeholder (the default).</summary>
    private const int VenueAllIndex = 0;

    /// <summary>ComboBox index of the "paper venues only" choice.</summary>
    private const int VenuePaperIndex = 1;

    /// <summary>ComboBox index of the "live venues only" choice.</summary>
    private const int VenueLiveIndex = 2;

    /// <summary>Decimal places of the displayed money aggregates.</summary>
    private const int MoneyDecimals = 4;

    /// <summary>Decimal places of the displayed profit factor.</summary>
    private const int ProfitFactorDecimals = 2;

    /// <summary>Decimal places of the displayed win-rate percentage.</summary>
    private const int WinRateDecimals = 1;

    /// <summary>Decimal places of the displayed ratio tiles (Sharpe, Sortino, R).</summary>
    private const int RatioDecimals = 2;

    /// <summary>UTC hours the hourly performance table buckets over.</summary>
    private const int HourBucketCount = 24;

    /// <summary>Weekdays the weekday performance table buckets over.</summary>
    private const int WeekdayBucketCount = 7;

    #endregion Private Constants

    #region Private Fields

    private readonly IOrderStatsQuery _query;
    private readonly ITradingAdapter _trading;
    private readonly ICsvExportService _csv;
    private readonly IStatementExportService _statementExport;
    private readonly IOrderHistoryStore _store;
    private readonly ILocalizationManager _localization;
    private readonly TradeExcursionReader _excursions;
    private readonly ActiveExchangeService _exchanges;
    private readonly ILogger<TradeStatsViewModel> _logger;

    /// <summary>Identity of the current scan; a newer apply supersedes the older scan's loop.</summary>
    private long _runId;

    /// <summary>
    /// The realized-trades table's backing list; the public property serves it directly.
    /// </summary>
    private List<TradeRealizedRowViewModel> _realizedRows = [];

    /// <summary>The hourly performance table's backing list; the public property serves it directly.</summary>
    private IReadOnlyList<TradeStatsBucketRowViewModel> _hourRows = [];

    /// <summary>The weekday performance table's backing list; the public property serves it directly.</summary>
    private IReadOnlyList<TradeStatsBucketRowViewModel> _weekdayRows = [];

    #endregion Private Fields

    #region Properties

    private string _symbolFilterText = string.Empty;

    /// <summary>Symbol filter text; empty (the default) means no symbol filter.</summary>
    public string SymbolFilterText
    {
        get => _symbolFilterText;
        set => this.RaiseAndSetIfChanged(ref _symbolFilterText, value);
    }

    private string? _exchangeFilterText;

    /// <summary>Exchange filter text; empty/null means every exchange.</summary>
    public string? ExchangeFilterText
    {
        get => _exchangeFilterText;
        set => this.RaiseAndSetIfChanged(ref _exchangeFilterText, value);
    }

    private int _venueIndex = VenueAllIndex;

    /// <summary>Selected paper-live tag: all venues, paper only or live only.</summary>
    public int VenueIndex
    {
        get => _venueIndex;
        set => this.RaiseAndSetIfChanged(ref _venueIndex, value);
    }

    private DateTimeOffset? _fromDate;

    /// <summary>Range start date (inclusive, UTC midnight).</summary>
    public DateTimeOffset? FromDate
    {
        get => _fromDate;
        set => this.RaiseAndSetIfChanged(ref _fromDate, value);
    }

    private string _tagLaneFilterText = string.Empty;

    /// <summary>
    /// The tag lane filter text (T-211): the token a closing journal row's
    /// tags must carry for its outcome to book. Empty (the default) means
    /// no lane — every closing fill books. The token rides the accumulator
    /// at Apply time: editing tags re-evaluates on the next Apply, the lane
    /// is a scan scope, not a live filter.
    /// </summary>
    public string TagLaneFilterText
    {
        get => _tagLaneFilterText;
        set => this.RaiseAndSetIfChanged(ref _tagLaneFilterText, value);
    }

    private DateTimeOffset? _toDate;

    /// <summary>Range end date (its whole day is included; the filter upper bound is the next midnight, exclusive).</summary>
    public DateTimeOffset? ToDate
    {
        get => _toDate;
        set => this.RaiseAndSetIfChanged(ref _toDate, value);
    }

    private string _tradesText = ZerosText;

    /// <summary>Closing trades ingested so far (each closing fill is one trade outcome).</summary>
    public string TradesText
    {
        get => _tradesText;
        private set => this.RaiseAndSetIfChanged(ref _tradesText, value);
    }

    private string _winsText = ZerosText;

    /// <summary>Closing trades that realized a positive PnL.</summary>
    public string WinsText
    {
        get => _winsText;
        private set => this.RaiseAndSetIfChanged(ref _winsText, value);
    }

    private string _lossesText = ZerosText;

    /// <summary>Closing trades that realized a negative PnL.</summary>
    public string LossesText
    {
        get => _lossesText;
        private set => this.RaiseAndSetIfChanged(ref _lossesText, value);
    }

    private string _winRateText = NotAvailableText;

    /// <summary>Win rate over the decided trades; a dash while nothing decided yet.</summary>
    public string WinRateText
    {
        get => _winRateText;
        private set => this.RaiseAndSetIfChanged(ref _winRateText, value);
    }

    private string _profitFactorText = NotAvailableText;

    /// <summary>Profit factor; a dash with nothing won or lost, infinity rendered unbounded.</summary>
    public string ProfitFactorText
    {
        get => _profitFactorText;
        private set => this.RaiseAndSetIfChanged(ref _profitFactorText, value);
    }

    private string _averageWinnerText = NotAvailableText;

    /// <summary>Average realized winner; a dash while no winner exists.</summary>
    public string AverageWinnerText
    {
        get => _averageWinnerText;
        private set => this.RaiseAndSetIfChanged(ref _averageWinnerText, value);
    }

    private string _averageLoserText = NotAvailableText;

    /// <summary>Average realized loser (negative); a dash while no loser exists.</summary>
    public string AverageLoserText
    {
        get => _averageLoserText;
        private set => this.RaiseAndSetIfChanged(ref _averageLoserText, value);
    }

    private string _maxDrawdownText = NotAvailableText;

    /// <summary>Maximum peak-to-trough decline of the equity curve so far.</summary>
    public string MaxDrawdownText
    {
        get => _maxDrawdownText;
        private set => this.RaiseAndSetIfChanged(ref _maxDrawdownText, value);
    }

    private string _realizedText = NotAvailableText;

    /// <summary>Total realized PnL over the scope so far.</summary>
    public string RealizedText
    {
        get => _realizedText;
        private set => this.RaiseAndSetIfChanged(ref _realizedText, value);
    }

    private string _volumeText = NotAvailableText;

    /// <summary>Total notional volume traded by the ingested fills.</summary>
    public string VolumeText
    {
        get => _volumeText;
        private set => this.RaiseAndSetIfChanged(ref _volumeText, value);
    }

    private string _sharpeText = NotAvailableText;

    /// <summary>Per-trade Sharpe ratio over the booked PnLs; the unbounded render speaks the mean's sign.</summary>
    public string SharpeText
    {
        get => _sharpeText;
        private set => this.RaiseAndSetIfChanged(ref _sharpeText, value);
    }

    private string _sortinoText = NotAvailableText;

    /// <summary>Per-trade Sortino ratio over the booked PnLs; the unbounded render speaks the mean's sign.</summary>
    public string SortinoText
    {
        get => _sortinoText;
        private set => this.RaiseAndSetIfChanged(ref _sortinoText, value);
    }

    private string _averageRText = NotAvailableText;

    /// <summary>Average R-multiple over the trades that carry a stop context; a dash while none does.</summary>
    public string AverageRText
    {
        get => _averageRText;
        private set => this.RaiseAndSetIfChanged(ref _averageRText, value);
    }

    private string _bestRText = NotAvailableText;

    /// <summary>The best R-multiple booked; a dash while no trade carries a stop context.</summary>
    public string BestRText
    {
        get => _bestRText;
        private set => this.RaiseAndSetIfChanged(ref _bestRText, value);
    }

    private string _worstRText = NotAvailableText;

    /// <summary>The worst R-multiple booked; a dash while no trade carries a stop context.</summary>
    public string WorstRText
    {
        get => _worstRText;
        private set => this.RaiseAndSetIfChanged(ref _worstRText, value);
    }

    /// <summary>
    /// The hourly performance table's rows (T-251): the 24 UTC hour buckets
    /// with the booked trades' outcomes. Fresh list per publish, like the
    /// realized table.
    /// </summary>
    public IReadOnlyList<TradeStatsBucketRowViewModel> HourRows
    {
        get => _hourRows;
        private set => this.RaiseAndSetIfChanged(ref _hourRows, value);
    }

    /// <summary>
    /// The weekday performance table's rows (T-251): the seven weekday
    /// buckets with the booked trades' outcomes. Fresh list per publish.
    /// </summary>
    public IReadOnlyList<TradeStatsBucketRowViewModel> WeekdayRows
    {
        get => _weekdayRows;
        private set => this.RaiseAndSetIfChanged(ref _weekdayRows, value);
    }

    private IReadOnlyList<EquityCurvePoint> _equityPoints = [];

    /// <summary>The cumulative realized-PnL curve so far (a fresh snapshot per published page).</summary>
    public IReadOnlyList<EquityCurvePoint> EquityPoints
    {
        get => _equityPoints;
        private set => this.RaiseAndSetIfChanged(ref _equityPoints, value);
    }

    private bool _hasTrades;

    /// <summary>True once at least one closing trade exists; drives the honest empty state.</summary>
    public bool HasTrades
    {
        get => _hasTrades;
        private set => this.RaiseAndSetIfChanged(ref _hasTrades, value);
    }

    /// <summary>
    /// The realized-trades table's rows (T-211): every booked close with its
    /// journal annotations, in booking order. Fresh list per publish, like
    /// the equity curve — an applied edit replaces the row instance.
    /// </summary>
    public IReadOnlyList<TradeRealizedRowViewModel> RealizedRows
    {
        get => _realizedRows;
        private set
        {
            // Callers always pass a fresh list; assignment replaces it whole
            // (an applied edit swaps one row instance inside the fresh copy).
            _realizedRows = value is List<TradeRealizedRowViewModel> list
                ? list
                : [.. value];
            this.RaisePropertyChanged(nameof(RealizedRows));
            this.RaisePropertyChanged(nameof(HasRealizedRows));
        }
    }

    /// <summary>True while the realized table has rows; drives its empty state.</summary>
    public bool HasRealizedRows => _realizedRows.Count > 0;

    private string _scanStateText = string.Empty;

    /// <summary>Scan progress: the fills scanned so far, or the completed-scan wording.</summary>
    public string ScanStateText
    {
        get => _scanStateText;
        private set => this.RaiseAndSetIfChanged(ref _scanStateText, value);
    }

    private string? _errorMessage;

    /// <summary>Last flow error (a rejected filter or a failed page read).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (ErrorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text; drives the error line visibility.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    /// <summary>Applies the current filters: restarts the paged scan from scratch.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; }

    /// <summary>Opens the note-and-tags dialog for one realized row (T-211); the applied result lands in the journal.</summary>
    public ReactiveCommand<TradeRealizedRowViewModel, RxVoid> EditRealizedRowCommand { get; }

    /// <summary>Exports the traded surface's current open positions as a positions CSV file (T-210).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportPositionsCommand { get; }

    /// <summary>Exports the statement bundle (orders + fills + realized ledger + balances, CSVs plus a manifest) under the window's filter (T-210).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportStatementCommand { get; }

    /// <summary>Asks the view for the positions CSV destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SavePositionsCsvRequested { get; }

    /// <summary>Asks the view for the statement manifest destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SaveStatementRequested { get; }

    /// <summary>
    /// Asks the view to show the note-and-tags dialog over one realized row
    /// (T-211); the applied result comes back, null closes without one.
    /// </summary>
    public Interaction<TradeRealizedRowViewModel, TradeNoteEditResult?> EditRealizedRowRequested { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a trade statistics view model. The view model is transient: a
    /// fresh instance scans the journal state as it is right now. The
    /// excursion reader rides the local candle cache (T-251), and the
    /// exchange facade names the fallback lanes the cache reads fall
    /// through when a trade's own venue key holds no series.
    /// </summary>
    public TradeStatsViewModel(
        IOrderStatsQuery query,
        ITradingAdapter trading,
        ICsvExportService csv,
        IStatementExportService statementExport,
        IOrderHistoryStore store,
        ILocalizationManager localization,
        TradeExcursionReader excursions,
        ActiveExchangeService exchanges,
        ILogger<TradeStatsViewModel> logger)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _statementExport = statementExport ?? throw new ArgumentNullException(nameof(statementExport));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _excursions = excursions ?? throw new ArgumentNullException(nameof(excursions));
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ApplyCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleApplyAsync(ct));
        EditRealizedRowCommand = ReactiveCommand.CreateFromTask<TradeRealizedRowViewModel, RxVoid>(
            (row, ct) => HandleEditRealizedRowAsync(row, ct));
        ExportPositionsCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleExportPositionsAsync(ct));
        ExportStatementCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleExportStatementAsync(ct));
        SavePositionsCsvRequested = new Interaction<string, string?>();
        SaveStatementRequested = new Interaction<string, string?>();
        EditRealizedRowRequested = new Interaction<TradeRealizedRowViewModel, TradeNoteEditResult?>();
    }

    #endregion Constructor

    #region Private Methods

    /// <summary>
    /// The paged scan: fresh accumulator, page after page until the store
    /// runs out, publishing refined statistics after every page. A newer
    /// apply supersedes the running loop at its next page boundary.
    /// </summary>
    private async Task<RxVoid> HandleApplyAsync(CancellationToken ct)
    {
        if (!TryBuildFilter(out var filter, out var invalidSymbol))
        {
            ErrorMessage = _localization.GetString(InvalidSymbolKey, invalidSymbol);
            return RxVoid.Default;
        }

        // A fresh attempt replaces whatever the previous attempt reported,
        // and the tiles fall back to their honest zero state until the first
        // page publishes.
        ErrorMessage = null;
        ResetStats();

        var runId = ++_runId;
        var accumulator = new TradeStatsAccumulator(TagLaneFilterText);
        OrderPageRow? last = null;
        long scanned = 0;

        try
        {
            while (true)
            {
                var page = await _query.ReadPageAsync(filter, last?.Order, PageSize, ct);

                if (runId != _runId)
                {
                    // A newer apply took over: publish nothing further. The
                    // fetched page stays uncounted — no garbled totals.
                    return RxVoid.Default;
                }

                if (page.Count == 0)
                {
                    break;
                }

                accumulator.Ingest(page);
                last = page[^1];
                scanned += page.Count;
                Publish(accumulator, isFinal: false, scanned: scanned);
            }

            if (runId == _runId)
            {
                Publish(accumulator, isFinal: true, scanned: scanned);
                await DeriveExcursionsAsync(runId, accumulator, scanned, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The window closed (or the command's execution subscription was
            // disposed) mid-scan: stop quietly, no error line, no log noise.
        }
        catch (Exception ex)
        {
            // Broader than the adapter surfaces: a local file/database fault
            // can arrive as anything (IO, SQLite, EF); the window stays
            // usable with the error line and keeps the last honest snapshot.
            _logger.LogError(ex, "Trade statistics window: scan failed");
            ErrorMessage = _localization.GetString(LoadFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The excursion phase (T-251), after the scan's final publish: the
    /// accumulator's trade facts read their windows' candles from the local
    /// cache (read-only, off the trading hot path) and the realized table
    /// republishes with the per-trade MAE/MFE columns filled. The lane is
    /// bookkeeping-grade: a fault logs and the table keeps the R columns it
    /// already carries, never an error line over a completed scan. A newer
    /// apply supersedes the pass at its own end.
    /// </summary>
    private async Task DeriveExcursionsAsync(
        long runId,
        TradeStatsAccumulator accumulator,
        long scanned,
        CancellationToken ct)
    {
        var facts = accumulator.GetTradeFacts();
        if (facts.Count == 0)
        {
            return;
        }

        ScanStateText = _localization.GetString(ExcursionStateKey);
        try
        {
            var derived = await _excursions.DeriveAsync(facts, ComposeAdapterCandidates(facts), ct);
            if (runId != _runId)
            {
                // A newer apply took over mid-pass: the merged table would
                // lie over the newer scan's rows — publish nothing.
                return;
            }

            PublishRealizedRows(accumulator, BuildExcursionMap(derived));
            _logger.LogInformation(
                "Trade statistics window: derived {Excursions} trade excursion(s) from the cached candles",
                derived.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the cache surfaces: the read rides the local
            // SQLite; the window stays usable, the MAE/MFE dashes stand.
            _logger.LogWarning(
                ex,
                "Trade statistics window: the excursion derivation failed; the MAE/MFE columns stay empty");
        }
        finally
        {
            if (runId == _runId)
            {
                // The pass's own state line hands the finished wording back.
                ScanStateText = _localization.GetString(ScannedKey, scanned);
            }
        }
    }

    /// <summary>
    /// The excursion reads' adapter-id fall-through, in priority order: the
    /// trades' own venue ids (a live trade's exchange id IS the cache key),
    /// then the facade's standing lane (the one the chart most likely
    /// watched), then the rest of the registered market-data lanes.
    /// </summary>
    private IReadOnlyList<string> ComposeAdapterCandidates(IReadOnlyList<TradeStatsTradeFact> facts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var id in facts.Select(static fact => fact.ExchangeId))
        {
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        if (seen.Add(_exchanges.Current.Id))
        {
            ids.Add(_exchanges.Current.Id);
        }

        foreach (var adapter in _exchanges.Registered)
        {
            if (seen.Add(adapter.Id))
            {
                ids.Add(adapter.Id);
            }
        }

        return ids;
    }

    /// <summary>Index the derived excursions by the ledger join's (exchange, order) key.</summary>
    private static Dictionary<string, TradeExcursionFact> BuildExcursionMap(
        IReadOnlyList<TradeExcursionFact> derived)
    {
        var map = new Dictionary<string, TradeExcursionFact>(StringComparer.Ordinal);
        foreach (var fact in derived)
        {
            map[JoinKey(fact.ExchangeId, fact.ClosingOrderId)] = fact;
        }

        return map;
    }

    /// <summary>The (exchange id, closing order id) key the ledger, the facts and the excursions join on.</summary>
    private static string JoinKey(string exchangeId, string orderId) => $"{exchangeId}|{orderId}";

    /// <summary>
    /// Republish the realized table with the derived MAE/MFE columns joined
    /// in (T-251): the ledger rebuilds from the accumulator as in every
    /// publish, and the excursion map fills the per-trade columns keyed by
    /// the ledger join — a trade without candle coverage keeps its dash.
    /// The bucket tables ride the same ledger and stay as the last publish
    /// built them (the excursions never change a bucket).
    /// </summary>
    private void PublishRealizedRows(
        TradeStatsAccumulator accumulator,
        IReadOnlyDictionary<string, TradeExcursionFact> excursions)
    {
        _realizedRows = accumulator.GetRealizedTrades()
            .Select(trade => TradeRealizedRowViewModel.From(
                trade,
                excursions.GetValueOrDefault(JoinKey(trade.ExchangeId, trade.ClosingOrderId))))
            .ToList();
        RealizedRows = _realizedRows;
    }

    /// <summary>
    /// The hourly bucket series (T-251): the booked trades sliced over the
    /// 24 UTC hours, every bucket rendered (zeros and a dash while nothing
    /// booked), the label the invariant "HH:00" form.
    /// </summary>
    private static IReadOnlyList<TradeStatsBucketRowViewModel> BuildHourRows(
        IReadOnlyList<StatementRealizedTrade> trades) =>
        BuildBucketRows(
            trades,
            static closedAt => closedAt.Hour,
            static hour => hour.ToString("00", CultureInfo.InvariantCulture) + ":00",
            HourBucketCount);

    /// <summary>
    /// The weekday bucket series (T-251): the booked trades sliced over the
    /// seven weekdays, Monday first, every bucket rendered, the label the
    /// localized abbreviated day name.
    /// </summary>
    private IReadOnlyList<TradeStatsBucketRowViewModel> BuildWeekdayRows(
        IReadOnlyList<StatementRealizedTrade> trades)
    {
        var labels = new[]
        {
            _localization.GetString(WeekdayMonKey),
            _localization.GetString(WeekdayTueKey),
            _localization.GetString(WeekdayWedKey),
            _localization.GetString(WeekdayThuKey),
            _localization.GetString(WeekdayFriKey),
            _localization.GetString(WeekdaySatKey),
            _localization.GetString(WeekdaySunKey),
        };

        return BuildBucketRows(
            trades,
            static closedAt => ((int)closedAt.DayOfWeek + 6) % 7,
            index => labels[index],
            WeekdayBucketCount);
    }

    /// <summary>
    /// Aggregate the booked ledger into one bucket series: the counts and
    /// the net realized PnL read the same signed convention the summary's
    /// tiles speak (a positive close wins, a negative loses, zero books as
    /// a trade that never decided), and an empty bucket keeps its label
    /// with zeros and a dash — the honest no-trades bucket, never hidden.
    /// </summary>
    private static IReadOnlyList<TradeStatsBucketRowViewModel> BuildBucketRows(
        IReadOnlyList<StatementRealizedTrade> trades,
        Func<DateTimeOffset, int> bucket,
        Func<int, string> label,
        int bucketCount)
    {
        var counts = new int[bucketCount];
        var wins = new int[bucketCount];
        var losses = new int[bucketCount];
        var pnls = new decimal[bucketCount];
        foreach (var trade in trades)
        {
            var index = bucket(trade.ClosedAt);
            counts[index]++;
            pnls[index] += trade.RealizedPnl;
            if (trade.RealizedPnl > 0m)
            {
                wins[index]++;
            }
            else if (trade.RealizedPnl < 0m)
            {
                losses[index]++;
            }
        }

        var rows = new TradeStatsBucketRowViewModel[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            rows[i] = new TradeStatsBucketRowViewModel
            {
                Bucket = label(i),
                Trades = counts[i].ToString(CultureInfo.InvariantCulture),
                Wins = wins[i].ToString(CultureInfo.InvariantCulture),
                Losses = losses[i].ToString(CultureInfo.InvariantCulture),
                Pnl = counts[i] == 0
                    ? NotAvailableText
                    : pnls[i].ToString("F4", CultureInfo.InvariantCulture),
                IsWinning = counts[i] > 0 && pnls[i] > 0m,
                IsLosing = counts[i] > 0 && pnls[i] < 0m,
            };
        }

        return rows;
    }

    /// <summary>
    /// The positions export (T-210): the traded surface's current open
    /// positions as one CSV file. The venue call comes first (the export's
    /// data source), the destination dialog second — a cancelled dialog
    /// exports nothing, a fault rides the window's error line.
    /// </summary>
    private async Task<RxVoid> HandleExportPositionsAsync(CancellationToken ct)
    {
        ErrorMessage = null;

        try
        {
            var positions = await _trading.GetPositionsAsync(ct);

            var path = await SavePositionsCsvRequested.Handle("positions.csv");
            if (path is null)
            {
                _logger.LogInformation(
                    "Trade statistics window: the positions export ended on a cancelled save dialog");
                return RxVoid.Default;
            }

            await _csv.ExportPositionsAsync(path, positions, ct);
            _logger.LogInformation(
                "Trade statistics window: exported positions.csv to {Path} ({Positions} positions)",
                path,
                positions.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the venue surfaces: the query can fault on the
            // transport, the write on any local IO state; the window stays
            // usable with its error line.
            _logger.LogError(ex, "Trade statistics window: the positions export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The statement export (T-210, streamed since T-219): one fresh full
    /// scan under the window's current filter becomes the statement bundle —
    /// orders.csv, fills.csv, realized-pnl.csv, balances.csv and the
    /// manifest. The order-side sections stream page by page straight into
    /// their CSVs during the scan (a six-figure journal never materializes
    /// the whole order list — the peak stays one page plus the realized
    /// ledger), the realized ledger and the balances ride the end-of-scan
    /// reads, and the manifest is written last, so an interrupted write
    /// leaves no manifest on disk. An empty range exports nothing (the
    /// localized refusal, never a broken bundle); the balance snapshot
    /// rides the traded surface's served state at export time.
    /// </summary>
    private async Task<RxVoid> HandleExportStatementAsync(CancellationToken ct)
    {
        ErrorMessage = null;

        try
        {
            if (!TryBuildFilter(out var filter, out var invalidSymbol))
            {
                ErrorMessage = _localization.GetString(InvalidSymbolKey, invalidSymbol);
                return RxVoid.Default;
            }

            // The first page is read before the save dialog: an empty range
            // is refused before the user is ever asked for a destination
            // (the localized refusal, never a broken bundle). One page of
            // memory is the streaming shape's whole journal buffer.
            var firstPage = await _query.ReadPageAsync(filter, null, PageSize, ct);
            if (firstPage.Count == 0)
            {
                _logger.LogInformation(
                    "Trade statistics window: the statement export found no orders in the filter");
                ErrorMessage = _localization.GetString(StatementEmptyKey);
                return RxVoid.Default;
            }

            var path = await SaveStatementRequested.Handle("trade-statement.json");
            if (path is null)
            {
                _logger.LogInformation(
                    "Trade statistics window: the statement export ended on a cancelled save dialog");
                return RxVoid.Default;
            }

            // The streaming bundle: the sections open (their headers land),
            // the scan appends page by page, the accumulator books the
            // realized rows on the way. The statement is the raw record:
            // no tag lane here — the bundle carries every trade the filter
            // scope saw, the lane only scopes the dashboard's aggregates.
            var ordersCount = 0;
            var accumulator = new TradeStatsAccumulator();
            await using var session = await _statementExport.BeginAsync(
                path,
                new StatementExportHeader(
                    SymbolFilter: filter.Symbol,
                    ExchangeFilter: filter.Exchange,
                    FromUtc: filter.From,
                    ToUtc: filter.To,
                    IsPaperFilter: filter.IsPaper),
                ct);

            var firstOrders = firstPage.Select(row => row.Order).ToArray();
            await session.AppendOrdersPageAsync(firstOrders, ct);
            accumulator.Ingest(firstPage);
            ordersCount += firstOrders.Length;
            var last = firstPage[^1];
            while (true)
            {
                var page = await _query.ReadPageAsync(filter, last.Order, PageSize, ct);
                if (page.Count == 0)
                {
                    break;
                }

                var pageOrders = page.Select(row => row.Order).ToArray();
                await session.AppendOrdersPageAsync(pageOrders, ct);
                accumulator.Ingest(page);
                ordersCount += pageOrders.Length;
                last = page[^1];
            }

            // The end-of-scan reads: the balance snapshot rides the traded
            // surface's served state at export time, the accumulator hands
            // over the booked ledger, and Complete closes the sections and
            // writes the manifest LAST (the completeness marker).
            var balances = await _trading.GetBalancesAsync(ct);
            var tail = new StatementExportTail(
                GeneratedAt: DateTimeOffset.UtcNow,
                RealizedTrades: accumulator.GetRealizedTrades(),
                Balances: balances);
            await session.CompleteAsync(tail, ct);
            _logger.LogInformation(
                "Trade statistics window: exported the statement bundle to {Path} ({Orders} orders, {Trades} realized trades)",
                path,
                ordersCount,
                tail.RealizedTrades.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the journal reads: the balance query rides the
            // venue, the writes ride the local disk; the window stays
            // usable with its error line.
            _logger.LogError(ex, "Trade statistics window: the statement export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// One annotation edit (T-211): the view answers the dialog interaction
    /// (null closes without an applied edit), the applied result rides the
    /// journal write, and the realized table's row instance is replaced with
    /// the fresh annotations. The lane's aggregates re-evaluate on the next
    /// Apply — the lane is a scan scope, not a live filter. A refused write
    /// (the journal row is gone — a reset wiped it mid-session) surfaces the
    /// honest failure line, never a fabricated save.
    /// </summary>
    private async Task<RxVoid> HandleEditRealizedRowAsync(
        TradeRealizedRowViewModel row,
        CancellationToken ct)
    {
        ErrorMessage = null;

        try
        {
            var result = await EditRealizedRowRequested.Handle(row);
            if (result is null)
            {
                _logger.LogInformation(
                    "Trade statistics window: the trade note edit ended on a cancelled dialog");
                return RxVoid.Default;
            }

            var saved = await _store.SetTradeMetaAsync(
                row.ExchangeId,
                row.OrderId,
                result.Note,
                result.Tags,
                ct);
            if (!saved)
            {
                _logger.LogWarning(
                    "Trade statistics window: the trade meta write for {ExchangeId}/{OrderId} found no recorded order",
                    row.ExchangeId,
                    row.OrderId);
                ErrorMessage = _localization.GetString(EditFailedKey, "no recorded order");
                return RxVoid.Default;
            }

            var index = _realizedRows.IndexOf(row);
            if (index < 0)
            {
                // The table republished between open and apply: the edited
                // row instance is stale — the next scan serves the fresh
                // annotations, nothing to replace here.
                _logger.LogInformation(
                    "Trade statistics window: the edited trade row is stale, the annotations land with the next scan");
                return RxVoid.Default;
            }

            _realizedRows[index] = row with { Note = result.Note, Tags = result.Tags };
            RealizedRows = [.. _realizedRows];
            _logger.LogInformation(
                "Trade statistics window: trade meta saved for {ExchangeId}/{OrderId} (note {HasNote}, tags {Tags})",
                row.ExchangeId,
                row.OrderId,
                result.Note is not null,
                result.Tags ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the annotation flow: the journal write rides the
            // local disk; the window stays usable with its error line.
            _logger.LogError(ex, "Trade statistics window: the trade note edit failed");
            ErrorMessage = _localization.GetString(EditFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// Build the read filter from the current editable state. An unparsable
    /// symbol text is a returned error, never a silently degraded filter.
    /// </summary>
    private bool TryBuildFilter(out OrderStatsFilter filter, out string invalidInput)
    {
        invalidInput = string.Empty;
        filter = new OrderStatsFilter(null, null, null, null, null);

        var symbolText = SymbolFilterText.Trim();
        if (symbolText.Length > 0)
        {
            if (!TryNormalizeSymbolText(symbolText, out var normalized, out invalidInput))
            {
                return false;
            }

            filter = filter with { Symbol = normalized };
        }

        var exchangeText = ExchangeFilterText?.Trim();
        if (!string.IsNullOrEmpty(exchangeText))
        {
            filter = filter with { Exchange = exchangeText };
        }

        if (FromDate is { } from)
        {
            filter = filter with { From = from.UtcDateTime };
        }

        if (ToDate is { } to)
        {
            filter = filter with { To = to.UtcDateTime.AddDays(1) };
        }

        filter = filter with
        {
            IsPaper = VenueIndex switch
            {
                VenuePaperIndex => true,
                VenueLiveIndex => false,
                _ => null,
            },
        };

        return true;
    }

    /// <summary>
    /// Normalize the symbol filter text through the Core record's normalized
    /// spelling. Embedded whitespace and empty sides around the separator
    /// never parse to something filterable — the text is reported back.
    /// </summary>
    private static bool TryNormalizeSymbolText(string text, out string normalized, out string invalidInput)
    {
        normalized = string.Empty;
        invalidInput = text;

        foreach (var part in text.Split('/'))
        {
            if (part.Length == 0 || part.Trim().Length != part.Length)
            {
                return false;
            }
        }

        try
        {
            normalized = Symbol.Parse(text).Normalized;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Fall the displayed state back to the honest zero/no-trades baseline.</summary>
    private void ResetStats()
    {
        TradesText = ZerosText;
        WinsText = ZerosText;
        LossesText = ZerosText;
        WinRateText = NotAvailableText;
        ProfitFactorText = NotAvailableText;
        AverageWinnerText = NotAvailableText;
        AverageLoserText = NotAvailableText;
        MaxDrawdownText = NotAvailableText;
        RealizedText = NotAvailableText;
        VolumeText = NotAvailableText;
        SharpeText = NotAvailableText;
        SortinoText = NotAvailableText;
        AverageRText = NotAvailableText;
        BestRText = NotAvailableText;
        WorstRText = NotAvailableText;
        EquityPoints = [];
        RealizedRows = [];
        HourRows = [];
        WeekdayRows = [];
        HasTrades = false;
        ScanStateText = string.Empty;
    }

    /// <summary>
    /// Publish one scan snapshot: the tiles, the equity points, the realized
    /// table and the honest progress line. The realized rows rebuild from
    /// the accumulator's ledger each publish — a fresh list, like the
    /// equity curve, so a snapshot never sees later rows.
    /// </summary>
    private void Publish(TradeStatsAccumulator accumulator, bool isFinal, long scanned)
    {
        var summary = accumulator.Snapshot();
        TradesText = FormatCount(summary.Trades);
        WinsText = FormatCount(summary.Wins);
        LossesText = FormatCount(summary.Losses);

        WinRateText = summary.WinRate is { } winRate
            ? FormatPercent(winRate)
            : NotAvailableText;

        // Unbounded profit factor: wins exist with no losses; a dash stands
        // while neither side of the ledger was booked at all.
        ProfitFactorText = summary.ProfitFactor is { } profitFactor
            ? FormatMoney(profitFactor, ProfitFactorDecimals)
            : summary.Wins > 0 ? UnboundedText : NotAvailableText;

        AverageWinnerText = summary.AverageWinner is { } winner
            ? FormatMoney(winner, MoneyDecimals)
            : NotAvailableText;

        AverageLoserText = summary.AverageLoser is { } loser
            ? FormatMoney(loser, MoneyDecimals)
            : NotAvailableText;

        MaxDrawdownText = FormatMoney(summary.MaxDrawdown, MoneyDecimals);
        RealizedText = FormatMoney(summary.RealizedTotal, MoneyDecimals);
        VolumeText = FormatMoney(summary.Volume, MoneyDecimals);
        SharpeText = FormatSharpeText(summary);
        SortinoText = FormatSortinoText(summary);
        AverageRText = FormatRatio(summary.AverageRMultiple);
        BestRText = FormatRatio(summary.BestRMultiple);
        WorstRText = FormatRatio(summary.WorstRMultiple);

        EquityPoints = summary.Equity;
        HasTrades = summary.Trades > 0;
        var ledger = accumulator.GetRealizedTrades();
        _realizedRows = ledger
            .Select(TradeRealizedRowViewModel.From)
            .ToList();
        RealizedRows = _realizedRows;
        HourRows = BuildHourRows(ledger);
        WeekdayRows = BuildWeekdayRows(ledger);
        ScanStateText = isFinal
            ? _localization.GetString(ScannedKey, scanned)
            : _localization.GetString(ScanningKey, scanned);
    }

    private static string FormatCount(int count) =>
        count.ToString(CultureInfo.InvariantCulture);

    private static string FormatMoney(decimal value, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.InvariantCulture);

    private static string FormatPercent(decimal fraction) =>
        (fraction * 100).ToString($"F{WinRateDecimals}", CultureInfo.InvariantCulture)
        + WinRateSuffix;

    /// <summary>The ratio tiles' numeric form (two invariant decimals); a dash while nothing carries the ratio.</summary>
    private static string FormatRatio(decimal? ratio) => ratio is { } value
        ? value.ToString($"F{RatioDecimals}", CultureInfo.InvariantCulture)
        : NotAvailableText;

    /// <summary>
    /// The Sharpe tile's render (T-251): the ratio while the accumulator
    /// computed it, the unbounded glyph by the mean's sign while the series
    /// is real but carries no spread at all (an identical-return series),
    /// the dash before a decided series exists.
    /// </summary>
    private static string FormatSharpeText(TradeStatsSummary summary) =>
        summary.Sharpe is { } ratio
            ? FormatRatio(ratio)
            : RenderUnboundedByMean(summary, minTrades: 2);

    /// <summary>
    /// The Sortino tile's render (T-251): the ratio while computed, the
    /// unbounded glyph by the mean's sign while the series is real but
    /// carries no downside at all, the dash before any decided trade.
    /// </summary>
    private static string FormatSortinoText(TradeStatsSummary summary) =>
        summary.Sortino is { } ratio
            ? FormatRatio(ratio)
            : RenderUnboundedByMean(summary, minTrades: 1);

    /// <summary>
    /// The unbounded ratio's honest render: a real series with no
    /// denominator spread renders the infinity glyph by the mean's sign;
    /// a zero mean or no series renders the dash (0/0 is undefined, never
    /// a fabricated infinity).
    /// </summary>
    private static string RenderUnboundedByMean(TradeStatsSummary summary, int minTrades)
    {
        if (summary.Trades < minTrades || summary.RealizedTotal == 0m)
        {
            return NotAvailableText;
        }

        return summary.RealizedTotal > 0 ? UnboundedText : NegativeUnboundedText;
    }

    #endregion Private Methods

    #region Display Constants

    /// <summary>The dash shown while a stat cannot be computed at all.</summary>
    private const string NotAvailableText = "—";

    /// <summary>Count tiles start at "0" before the first page publishes.</summary>
    private const string ZerosText = "0";

    /// <summary>The unbounded profit-factor glyph.</summary>
    private const string UnboundedText = "∞";

    /// <summary>The negative-unbounded ratio glyph (a losing no-downside series).</summary>
    private const string NegativeUnboundedText = "-∞";

    /// <summary>Win-rate values print as a percentage.</summary>
    private const string WinRateSuffix = " %";

    #endregion Display Constants
}
