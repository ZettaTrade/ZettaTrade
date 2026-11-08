namespace ZettaTrade.App.Services;

using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Follows the workspace through its streams and snapshots it for the chat:
/// every symbol selection switches the ticker and the order book feeds
/// (through the active-exchange facade, so an exchange switch re-attaches
/// automatically), a price tick and the time-frame stream update the cached
/// snapshot, and the enabled indicator setup rides the persisted settings
/// (read once at initialization, re-applied live from the changes stream).
/// <see cref="Current"/> is the whole truth there is: a part that never
/// arrived stays absent, and a symbol-less workspace renders no time frame
/// or market fact even if a choice was set (the chat shows the trader's
/// market or nothing, per part). All subscriptions live for the
/// application's lifetime (the service is an app-scoped singleton, like the
/// panel ViewModels it feeds).
///
/// Concurrency discipline: every stream handler swaps ONE immutable snapshot
/// reference (a single write can never tear), and the ticker/book callbacks
/// only write facts they captured for the symbol still standing — a late
/// frame from a previous feed cannot lie through the next snapshot, so no
/// cross-thread gate is needed.
/// </summary>
public sealed class ChatContextService : IAsyncInitializable
{
    #region Constants

    /// <summary>How many top levels per side the snapshot keeps from each
    /// book frame: the preamble reads the top of the book (best first), so
    /// the cache holds exactly the shape the moment renders.</summary>
    public const int BookTopLevelCount = 3;

    /// <summary>The workspace chart's default indicator rows re-read on a
    /// fresh install (the canonical empty-only-SMA shape).</summary>
    private const string DefaultIndicatorRows = UiSettingsKeys.DefaultChartIndicators;

    #endregion

    #region Snapshot

    /// <summary>The immutable workspace parts covered by one reference swap;
    /// no other consumer exists, so the record stays private to the service.</summary>
    private sealed record WorkspaceSnapshot(
        Symbol? Symbol = null,
        TimeFrame? TimeFrame = null,
        decimal? LastPrice = null,
        decimal? PriceChange24hPercent = null,
        IReadOnlyList<PriceLevel>? BestBids = null,
        IReadOnlyList<PriceLevel>? BestAsks = null);

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ITradingAdapter? _tradingAdapter;
    private readonly ISettingsService? _settings;
    private readonly ILogger<ChatContextService> _logger;

    private WorkspaceSnapshot _workspace = new();
    private IDisposable? _tickerSubscription;
    private IDisposable? _bookSubscription;

    /// <summary>The enabled indicator setup rows (a read's result once any
    /// read happened; before the first read the part stays honestly absent —
    /// null is "unknown", an empty list is "the user runs none").</summary>
    private IReadOnlyList<ChatIndicatorRow>? _indicatorRows;
    private bool _indicatorRowsLoaded;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new chat context service. The symbol stream, the time-frame
    /// stream, the market data facade and the optional market-moment sources
    /// (the trading venue and the settings the indicator setup survives in)
    /// are wired by the composition root — the same seams the order book and
    /// the chart already use. The optional sources carry the market-context
    /// facts: when either is missing, <see cref="Current"/> degrades to the
    /// pre-moment shape (the absent parts render nothing downstream).
    /// </summary>
    /// <param name="marketData">The active-exchange facade (auto-switching
    /// ticker, book and exchange name).</param>
    /// <param name="symbolSelections">Every confirmed workspace symbol.</param>
    /// <param name="timeFrameSelections">Every workspace time-frame change
    /// (the current value arrives at subscribe time).</param>
    /// <param name="logger">Injected class logger.</param>
    /// <param name="tradingAdapter">The venue actually trading the
    /// workspace; without it the paper/live classification stays absent.</param>
    /// <param name="settings">The app settings service; without it the
    /// indicator setup part stays absent.</param>
    public ChatContextService(
        IMarketDataAdapter marketData,
        IObservable<Symbol> symbolSelections,
        IObservable<TimeFrame> timeFrameSelections,
        ILogger<ChatContextService> logger,
        ITradingAdapter? tradingAdapter = null,
        ISettingsService? settings = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tradingAdapter = tradingAdapter;
        _settings = settings;

        symbolSelections = symbolSelections ?? throw new ArgumentNullException(nameof(symbolSelections));
        timeFrameSelections = timeFrameSelections ?? throw new ArgumentNullException(nameof(timeFrameSelections));

        symbolSelections.Subscribe(ApplySymbol);
        timeFrameSelections.Subscribe(ApplyTimeFrame);

        if (_settings is not null)
        {
            _settings.Changes
                .Where(change => change.Section == UiSettingsKeys.Section &&
                                 change.Key == UiSettingsKeys.ChartIndicators)
                .Select(change => change.NewValue)
                .OfType<string>()
                .Subscribe(
                    ParseIndicatorRows,
                    ex => _logger.LogError(
                        ex,
                        "The chat indicator-setup stream stopped unexpectedly"));
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// The workspace as it stands right now: the active exchange's display
    /// name (read fresh on every render, so a facade switch re-renders the
    /// head), the selected symbol and time frame, the latest ticker facts
    /// (price, 24h change), the top book levels, the enabled indicator setup
    /// and the paper/live venue classification. Parts absent from the
    /// streams stay absent from the snapshot.
    /// </summary>
    public ChatContext Current
    {
        get
        {
            var workspace = _workspace;
            var hasSymbol = workspace.Symbol is not null;

            return new ChatContext
            {
                ExchangeName = _marketData.Name,
                Symbol = workspace.Symbol?.Normalized,
                TimeFrame = hasSymbol ? workspace.TimeFrame : null,
                LastPrice = hasSymbol ? workspace.LastPrice : null,
                PriceChange24hPercent = hasSymbol ? workspace.PriceChange24hPercent : null,
                BestBids = hasSymbol ? workspace.BestBids : null,
                BestAsks = hasSymbol ? workspace.BestAsks : null,
                Indicators = _indicatorRowsLoaded ? _indicatorRows : null,
                VenueId = _tradingAdapter?.Id ?? string.Empty,
                IsPaperVenue = _tradingAdapter is null
                    ? null
                    : string.Equals(
                        _tradingAdapter.Id,
                        PaperTradingAdapter.ExchangeId,
                        StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Read the persisted indicator setup once: the async settings read the
    /// constructor cannot block for. The last writer wins (this read and the
    /// changes stream race the same parse, an idempotent projection of the
    /// same JSON string, so either arriving first leaves the same rows).
    /// </summary>
    /// <param name="ct">The cancellation token of the startup walk.</param>
    /// <returns>The completed read.</returns>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_settings is null)
        {
            return;
        }

        var json = await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            DefaultIndicatorRows);
        ParseIndicatorRows(json);

        _logger.LogInformation(
            "Chat market context initialized: {IndicatorRowCount} enabled indicator row(s)",
            _indicatorRows?.Count ?? 0);
    }

    #endregion

    #region Stream Handlers

    /// <summary>
    /// A symbol change is a new market: the old feeds detach, the stale
    /// facts blank immediately (only the new market's next frames rebuild
    /// them), and the standing time frame stays the workspace's standing
    /// choice — rendered again only once a symbol exists. An adapter that
    /// serves no book stream is a degraded market, not a crash: the book
    /// part of the moment stays absent (the adapter's breach is logged),
    /// and the destructive lane never takes the ticker's down with it.
    /// </summary>
    private void ApplySymbol(Symbol symbol)
    {
        _tickerSubscription?.Dispose();
        _bookSubscription?.Dispose();
        _workspace = _workspace with
        {
            Symbol = symbol,
            LastPrice = null,
            PriceChange24hPercent = null,
            BestBids = null,
            BestAsks = null,
        };
        _tickerSubscription = _marketData
            .SubscribeTicker(symbol)
            .Subscribe(
                ticker => UpdateTicker(ticker, symbol),
                ex => _logger.LogError(
                    ex,
                    "The chat ticker stream failed for {Symbol}",
                    symbol.Normalized));
        var bookStream = _marketData.SubscribeOrderBook(symbol, BookTopLevelCount);
        _bookSubscription = bookStream?.Subscribe(
            book => UpdateBook(book, symbol),
            ex => _logger.LogError(
                ex,
                "The chat order book stream failed for {Symbol}",
                symbol.Normalized));
        if (bookStream is null)
        {
            _logger.LogWarning(
                "The exchange adapter serves no order-book stream for {Symbol}; the market context's book part stays absent",
                symbol.Normalized);
        }

        _logger.LogInformation("Chat context symbol: {Symbol}", symbol.Normalized);
    }

    /// <summary>
    /// The standing time-frame choice: the snapshot renders it only once a
    /// symbol exists (see <see cref="Current"/>), so a bootstrap frame never
    /// meets a stale symbol.
    /// </summary>
    private void ApplyTimeFrame(TimeFrame timeFrame) =>
        _workspace = _workspace with { TimeFrame = timeFrame };

    /// <summary>
    /// A tick writes its price and 24h change only if it belongs to the
    /// market the snapshot stands on: each subscription captured its own
    /// symbol, so a late tick arriving through an old feed is dropped
    /// instead of overwriting the new market's facts.
    /// </summary>
    private void UpdateTicker(Ticker ticker, Symbol feedSymbol)
    {
        var workspace = _workspace;
        if (!Equals(workspace.Symbol, feedSymbol))
        {
            return;
        }

        _workspace = workspace with
        {
            LastPrice = ticker.LastPrice,
            PriceChange24hPercent = ticker.PriceChange24hPercent,
        };
    }

    /// <summary>
    /// A book frame swaps the top levels only if it belongs to the standing
    /// market (the same captured-symbol guard the ticker rides): a side the
    /// frame carried empty renders as its market's withdrawal, never as the
    /// previous snapshot's leftovers.
    /// </summary>
    private void UpdateBook(OrderBook book, Symbol feedSymbol)
    {
        var workspace = _workspace;
        if (!Equals(workspace.Symbol, feedSymbol))
        {
            return;
        }

        _workspace = workspace with
        {
            BestBids = book.Bids.Count == 0 ? [] : book.Bids.Take(BookTopLevelCount).ToArray(),
            BestAsks = book.Asks.Count == 0 ? [] : book.Asks.Take(BookTopLevelCount).ToArray(),
        };
    }

    /// <summary>
    /// Parse the persisted indicator JSON into the enabled setup rows (the
    /// canonical <see cref="ChartIndicatorConfig"/> parser — its problems are
    /// logged and skipped exactly like the chart lane does): the workspace
    /// moment reflects the configured setup, never a fabricated row.
    /// </summary>
    private void ParseIndicatorRows(string? indicatorSettingsJson)
    {
        if (indicatorSettingsJson is null)
        {
            return;
        }

        var (configs, problems) = ChartIndicatorConfig.ParseList(indicatorSettingsJson);
        foreach (var problem in problems)
        {
            _logger.LogWarning("The chat indicator-setup row is skipped: {Problem}", problem);
        }

        _indicatorRows = [.. configs
            .Where(config => config.Enabled)
            .Select(config => new ChatIndicatorRow(config.Name, config.Period))];
        _indicatorRowsLoaded = true;
    }

    #endregion
}
