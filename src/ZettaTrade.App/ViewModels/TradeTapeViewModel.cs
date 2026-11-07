namespace ZettaTrade.App.ViewModels;

using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Trade tape (time and sales) panel state: a typed symbol input (free-form,
/// like the order book panel, so the tape still works offline), and the live
/// trades stream for the parsed symbol.
/// </summary>
/// <remarks>
/// Like <see cref="OrderBookViewModel"/>, this is an application-lifetime
/// singleton whose subscriptions are created in the constructor: WhenActivated
/// never fires for VMs bound as nested-panel DataContexts, and the tape must
/// stream from the first frame. The workspace selection (the chart's symbol
/// combo) is the owner and rewrites the typed input; the typed input stays
/// the free-form override and the last user action wins.
/// <see cref="Updates"/> stays raw (testable without the UI thread): the tape
/// widget consumes the stream itself (buffering, coalescing, pausing — its
/// own frame discipline), so the ViewModel ships no per-trade snapshot.
/// A new symbol cancels the previous stream (Rx <c>Switch</c>); a stream
/// failure ends the data for its symbol only (logged, never a default value)
/// and later symbol changes keep working.
/// </remarks>
public sealed class TradeTapeViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Symbol typed at start, so the tape streams live data immediately.</summary>
    public const string DefaultSymbolInput = "BTC/USDT";

    #endregion

    #region Properties

    private readonly ILogger<TradeTapeViewModel> _logger;

    private string _symbolInput = DefaultSymbolInput;

    /// <summary>Raw symbol text; a parse failure keeps the previous tape visible.</summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set => this.RaiseAndSetIfChanged(ref _symbolInput, value);
    }

    /// <summary>
    /// Live trades for the parsed symbol, switched so a new symbol cancels
    /// the previous stream. Raw — not UI-marshaled; the widget owns its own
    /// buffering and frame discipline.
    /// </summary>
    public IObservable<MarketTrade> Updates { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new trade tape ViewModel instance.
    /// </summary>
    public TradeTapeViewModel(
        IMarketDataAdapter marketData,
        IObservable<Symbol> workspaceSelections,
        ILogger<TradeTapeViewModel> logger)
    {
        _logger = logger;

        Updates = this.WhenAnyValue(x => x.SymbolInput)
            .Select(ToSymbol)
            .DistinctUntilChanged()
            .Where(symbol => symbol is not null)
            .Select(symbol => GuardStreamFailures(marketData.SubscribeTrades(symbol!)))
            .Switch();

        // The workspace combo owns the symbol: a selection rewrites the typed
        // input (RaiseAndSetIfChanged dedupes the no-change repeats) and the
        // trades switch chain follows. The ViewModel lives as long as the
        // application (singleton), so this subscription mirrors it.
        workspaceSelections.Subscribe(
            symbol =>
            {
                SymbolInput = symbol.Normalized;
                _logger.LogInformation("Workspace selection applied to the tape: {Symbol}", symbol.Normalized);
            },
            ex => _logger.LogError(ex, "The tape's workspace symbol stream stopped unexpectedly"));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Parse typed input into a normalized symbol; unexpected input yields
    /// null, which switches to no data (the widget keeps what it already has).
    /// </summary>
    private static Symbol? ToSymbol(string input)
    {
        var raw = input.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var parts = raw.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0))
        {
            return null;
        }

        try
        {
            return Symbol.Parse(raw);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stream failure ends the current symbol's data (an error is logged)
    /// but must not terminate the switch pipeline for later symbols.
    /// </summary>
    private IObservable<MarketTrade> GuardStreamFailures(IObservable<MarketTrade> source) =>
        source.Catch<MarketTrade, Exception>(ex =>
        {
            _logger.LogError(ex, "The trade stream ended with an error");
            return Observable.Empty<MarketTrade>();
        });

    #endregion
}
