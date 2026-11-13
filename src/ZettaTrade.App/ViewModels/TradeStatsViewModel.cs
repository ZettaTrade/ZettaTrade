namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
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
/// rows; the equity points bind the Skia chart. The export routes (T-210)
/// ride the window's data sources: the traded surface's positions as one
/// CSV, and the whole statement bundle (orders + fills + the fill-pair
/// realized ledger + a balances snapshot, CSVs plus a JSON manifest) under
/// the window's current filter — an empty range writes nothing, never a
/// broken file.
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

    #endregion Private Constants

    #region Private Fields

    private readonly IOrderStatsQuery _query;
    private readonly ITradingAdapter _trading;
    private readonly ICsvExportService _csv;
    private readonly IStatementExportService _statementExport;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<TradeStatsViewModel> _logger;

    /// <summary>Identity of the current scan; a newer apply supersedes the older scan's loop.</summary>
    private long _runId;

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

    /// <summary>Exports the traded surface's current open positions as a positions CSV file (T-210).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportPositionsCommand { get; }

    /// <summary>Exports the statement bundle (orders + fills + realized ledger + balances, CSVs plus a manifest) under the window's filter (T-210).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportStatementCommand { get; }

    /// <summary>Asks the view for the positions CSV destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SavePositionsCsvRequested { get; }

    /// <summary>Asks the view for the statement manifest destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SaveStatementRequested { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a trade statistics view model. The view model is transient: a
    /// fresh instance scans the journal state as it is right now.
    /// </summary>
    public TradeStatsViewModel(
        IOrderStatsQuery query,
        ITradingAdapter trading,
        ICsvExportService csv,
        IStatementExportService statementExport,
        ILocalizationManager localization,
        ILogger<TradeStatsViewModel> logger)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _statementExport = statementExport ?? throw new ArgumentNullException(nameof(statementExport));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ApplyCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleApplyAsync(ct));
        ExportPositionsCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleExportPositionsAsync(ct));
        ExportStatementCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => HandleExportStatementAsync(ct));
        SavePositionsCsvRequested = new Interaction<string, string?>();
        SaveStatementRequested = new Interaction<string, string?>();
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
        var accumulator = new TradeStatsAccumulator();
        Order? after = null;
        long scanned = 0;

        try
        {
            while (true)
            {
                var page = await _query.ReadPageAsync(filter, after, PageSize, ct);

                if (runId != _runId)
                {
                    // A newer apply took over: publish nothing further. The
                    // fetched page stays uncounted — no gorbled totals.
                    return RxVoid.Default;
                }

                if (page.Count == 0)
                {
                    break;
                }

                accumulator.Ingest(page);
                after = page[^1];
                scanned += page.Count;
                Publish(accumulator.Snapshot(), isFinal: false, scanned: scanned);
            }

            if (runId == _runId)
            {
                Publish(accumulator.Snapshot(), isFinal: true, scanned: scanned);
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
    /// The statement export (T-210): one fresh full scan under the window's
    /// current filter becomes the statement bundle — orders.csv, fills.csv,
    /// realized-pnl.csv, balances.csv and the manifest. An empty range
    /// exports nothing (the localized refusal, never a broken bundle); the
    /// manifest is written last, so an interrupted write leaves no manifest
    /// on disk. The balance snapshot rides the traded surface's served
    /// state at export time.
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

            // The fresh full scan: the bundle's orders collect whole (the
            // fill-pair ledger needs the full pass), the accumulator books
            // the realized rows on the way.
            var orders = new List<Order>();
            var accumulator = new TradeStatsAccumulator();
            Order? after = null;
            while (true)
            {
                var page = await _query.ReadPageAsync(filter, after, PageSize, ct);
                if (page.Count == 0)
                {
                    break;
                }

                orders.AddRange(page);
                accumulator.Ingest(page);
                after = page[^1];
            }

            if (orders.Count == 0)
            {
                // The empty range: nothing is written, never a broken file.
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

            var balances = await _trading.GetBalancesAsync(ct);
            var statement = new StatementExport(
                GeneratedAt: DateTimeOffset.UtcNow,
                SymbolFilter: filter.Symbol,
                ExchangeFilter: filter.Exchange,
                FromUtc: filter.From,
                ToUtc: filter.To,
                IsPaperFilter: filter.IsPaper,
                Orders: orders,
                RealizedTrades: accumulator.GetRealizedTrades(),
                Balances: balances);
            await _statementExport.WriteAsync(path, statement, ct);
            _logger.LogInformation(
                "Trade statistics window: exported the statement bundle to {Path} ({Orders} orders, {Trades} realized trades)",
                path,
                orders.Count,
                statement.RealizedTrades.Count);
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
        EquityPoints = [];
        HasTrades = false;
        ScanStateText = string.Empty;
    }

    /// <summary>Publish one scan snapshot: the tiles, the equity points and the honest progress line.</summary>
    private void Publish(TradeStatsSummary summary, bool isFinal, long scanned)
    {
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

        EquityPoints = summary.Equity;
        HasTrades = summary.Trades > 0;
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

    #endregion Private Methods

    #region Display Constants

    /// <summary>The dash shown while a stat cannot be computed at all.</summary>
    private const string NotAvailableText = "—";

    /// <summary>Count tiles start at "0" before the first page publishes.</summary>
    private const string ZerosText = "0";

    /// <summary>The unbounded profit-factor glyph.</summary>
    private const string UnboundedText = "∞";

    /// <summary>Win-rate values print as a percentage.</summary>
    private const string WinRateSuffix = " %";

    #endregion Display Constants
}
