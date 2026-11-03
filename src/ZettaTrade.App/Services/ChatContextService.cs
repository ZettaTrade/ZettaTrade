namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Follows the workspace through its streams and snapshots it for the chat:
/// every symbol selection switches the ticker feed (through the active-exchange
/// facade, so an exchange switch re-attaches automatically), a price tick and
/// the time-frame stream update the cached snapshot. <see cref="Current"/> is
/// the whole truth there is: a part that never arrived stays absent, and a
/// symbol-less workspace renders no time frame or price even if a choice was
/// set (the chat shows the trader's market or nothing, per part). All
/// subscriptions live for the application's lifetime (the service is an
/// app-scoped singleton, like the panel ViewModels it feeds).
///
/// Concurrency discipline: every stream handler swaps ONE immutable snapshot
/// reference (a single write can never tear), and the ticker callback only
/// writes a price it captured for the symbol still standing — a late tick
/// from a previous feed cannot lie through the next snapshot, so no
/// cross-thread gate is needed.
/// </summary>
public sealed class ChatContextService
{
    #region Snapshot

    /// <summary>The immutable workspace parts covered by one reference swap;
    /// no other consumer exists, so the record stays private to the service.</summary>
    private sealed record WorkspaceSnapshot(
        Symbol? Symbol = null,
        TimeFrame? TimeFrame = null,
        decimal? LastPrice = null);

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ILogger<ChatContextService> _logger;

    private WorkspaceSnapshot _workspace = new();
    private IDisposable? _tickerSubscription;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new chat context service. The symbol stream, the time-frame
    /// stream and the market data facade are wired by the composition root
    /// (the same seams the order book and the chart already use).
    /// </summary>
    /// <param name="marketData">The active-exchange facade (auto-switching
    /// ticker source and exchange name).</param>
    /// <param name="symbolSelections">Every confirmed workspace symbol.</param>
    /// <param name="timeFrameSelections">Every workspace time-frame change
    /// (the current value arrives at subscribe time).</param>
    /// <param name="logger">Injected class logger.</param>
    public ChatContextService(
        IMarketDataAdapter marketData,
        IObservable<Symbol> symbolSelections,
        IObservable<TimeFrame> timeFrameSelections,
        ILogger<ChatContextService> logger)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        symbolSelections = symbolSelections ?? throw new ArgumentNullException(nameof(symbolSelections));
        timeFrameSelections = timeFrameSelections ?? throw new ArgumentNullException(nameof(timeFrameSelections));

        symbolSelections.Subscribe(ApplySymbol);
        timeFrameSelections.Subscribe(ApplyTimeFrame);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The workspace as it stands right now: the active exchange's display
    /// name (read fresh on every render, so a facade switch re-renders the
    /// head), the selected symbol and time frame, the latest ticker price.
    /// Parts absent from the streams stay absent from the snapshot.
    /// </summary>
    public ChatContext Current
    {
        get
        {
            var workspace = _workspace;

            return new ChatContext
            {
                ExchangeName = _marketData.Name,
                Symbol = workspace.Symbol?.Normalized,
                TimeFrame = workspace.Symbol is null ? null : workspace.TimeFrame,
                LastPrice = workspace.Symbol is null ? null : workspace.LastPrice,
            };
        }
    }

    #endregion

    #region Stream Handlers

    /// <summary>
    /// A symbol change is a new market: the old feed detaches, the stale
    /// price blanks immediately (only the new market's next tick rebuilds
    /// it), and the standing time frame stays the workspace's standing
    /// choice — rendered again only once a symbol exists.
    /// </summary>
    private void ApplySymbol(Symbol symbol)
    {
        _tickerSubscription?.Dispose();
        _workspace = _workspace with { Symbol = symbol, LastPrice = null };
        _tickerSubscription = _marketData
            .SubscribeTicker(symbol)
            .Subscribe(
                ticker => UpdatePrice(ticker, symbol),
                ex => _logger.LogError(
                    ex,
                    "The chat ticker stream failed for {Symbol}",
                    symbol.Normalized));

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
    /// A tick writes its price only if it belongs to the market the snapshot
    /// stands on: each subscription captured its own symbol, so a late tick
    /// arriving through an old feed is dropped instead of overwriting the
    /// new market's price.
    /// </summary>
    private void UpdatePrice(Ticker ticker, Symbol feedSymbol)
    {
        var workspace = _workspace;
        if (!Equals(workspace.Symbol, feedSymbol))
        {
            return;
        }

        _workspace = workspace with { LastPrice = ticker.LastPrice };
    }

    #endregion
}
