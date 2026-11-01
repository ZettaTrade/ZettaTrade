namespace ZettaTrade.App.ViewModels;

using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Extensions;

/// <summary>
/// Order book panel state: a typed symbol input (free-form, like the New
/// Order dialog, so the panel still works offline), the live updates stream
/// for the parsed symbol, and the latest book snapshot for rendering.
/// </summary>
/// <remarks>
/// Like <see cref="StatusBarViewModel"/>, this is an application-lifetime
/// singleton whose subscriptions are created in the constructor: WhenActivated
/// never fires for VMs bound as nested-panel DataContexts, and the book must
/// stream from the first frame. The workspace selection (the chart's symbol
/// combo) is the owner and rewrites the typed input; the typed input stays
/// the free-form override and the last user action wins.
/// <see cref="Updates"/> stays raw (testable without the UI thread);
/// <see cref="Book"/> is assigned after the UI-thread hop. A new symbol
/// cancels the previous stream (Rx <c>Switch</c>); a stream failure ends the
/// data for its symbol only (logged, never a default value) and later symbol
/// changes keep working.
/// </remarks>
public sealed class OrderBookViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Symbol typed at start, so the book streams live data immediately.</summary>
    public const string DefaultSymbolInput = "BTC/USDT";

    #endregion

    #region Properties

    private readonly ILogger<OrderBookViewModel> _logger;
    private OrderBook? _book;

    private string _symbolInput = DefaultSymbolInput;

    /// <summary>Raw symbol text; a parse failure keeps the previous book visible.</summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set => this.RaiseAndSetIfChanged(ref _symbolInput, value);
    }

    /// <summary>Latest book snapshot (published to the UI thread).</summary>
    public OrderBook? Book
    {
        get => _book;
        private set => this.RaiseAndSetIfChanged(ref _book, value);
    }

    /// <summary>
    /// Live book updates for the parsed symbol, switched so a new symbol
    /// cancels the previous stream. Raw — not UI-marshaled; user-interface
    /// consumers marshal themselves.
    /// </summary>
    public IObservable<OrderBook> Updates { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new order book ViewModel instance.
    /// </summary>
    public OrderBookViewModel(
        IMarketDataAdapter marketData,
        IObservable<Symbol> workspaceSelections,
        ILogger<OrderBookViewModel> logger)
    {
        _logger = logger;

        Updates = this.WhenAnyValue(x => x.SymbolInput)
            .Select(ToSymbol)
            .DistinctUntilChanged()
            .Where(symbol => symbol is not null)
            .Select(symbol => GuardStreamFailures(marketData.SubscribeOrderBook(symbol!)))
            .Switch();

        // The workspace combo owns the symbol: a selection rewrites the typed
        // input (RaiseAndSetIfChanged dedupes the no-change repeats) and the
        // bars/book switch chain follows. The ViewModel lives as long as the
        // application (singleton), so this subscription mirrors it.
        workspaceSelections.Subscribe(
            symbol =>
            {
                SymbolInput = symbol.Normalized;
                _logger.LogInformation("Workspace selection applied: {Symbol}", symbol.Normalized);
            },
            ex => _logger.LogError(ex, "The workspace symbol stream stopped unexpectedly"));

        // The ViewModel lives as long as the application (singleton), so this
        // subscription mirrors it and needs no explicit disposal. The marshal
        // chain keeps <see cref="Book"/> on the Avalonia UI thread.
        Updates.ObserveOnUiThread()
            .Subscribe(
                book => Book = book,
                ex => _logger.LogError(ex, "The order book stream stopped unexpectedly"));
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
            return null;

        var parts = raw.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0))
            return null;

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
    private IObservable<OrderBook> GuardStreamFailures(IObservable<OrderBook> source) =>
        source.Catch<OrderBook, Exception>(ex =>
        {
            _logger.LogError(ex, "The order book stream ended with an error");
            return Observable.Empty<OrderBook>();
        });

    #endregion
}
