namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

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
/// <para>
/// The T-246 imbalance chip rides the same marshaled lane: every served
/// snapshot re-weighs the window's ratio (the pure
/// <see cref="BookImbalance.Ratio"/>, allocation-free), the optional
/// smoothing carries across updates and the knob changes re-weigh the
/// standing book; a symbol switch resets the carried read — the chip blanks
/// until the new symbol's first book lands, so the old symbol's depth
/// distribution never blends into the new one's.
/// </para>
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

    private decimal _imbalance;

    private string _imbalanceText = string.Empty;

    private int _imbalanceWindow = BookImbalance.DefaultWindow;

    private bool _imbalanceSmoothed;

    /// <summary>The carried previous read the smoothing blends from (null = the stream's first read).</summary>
    private decimal? _smoothedRead;

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
    /// The served snapshot's window imbalance ratio (the T-246 chip's read,
    /// in [−1, +1]); zero until the first book lands.
    /// </summary>
    public decimal Imbalance
    {
        get => _imbalance;
        private set => this.RaiseAndSetIfChanged(ref _imbalance, value);
    }

    /// <summary>
    /// The chip's invariant spelling of <see cref="Imbalance"/> (a signed
    /// two-decimal form); empty until the first book lands.
    /// </summary>
    public string ImbalanceText
    {
        get => _imbalanceText;
        private set => this.RaiseAndSetIfChanged(ref _imbalanceText, value);
    }

    /// <summary>
    /// The depth window (price levels per side) the ratio weighs; a change
    /// re-weighs the standing book live. Below one level the read is neutral
    /// (the calculator's own honest-off shape).
    /// </summary>
    public int ImbalanceWindow
    {
        get => _imbalanceWindow;
        set
        {
            this.RaiseAndSetIfChanged(ref _imbalanceWindow, value);
            RecomputeImbalance();
        }
    }

    /// <summary>
    /// Whether the per-update smoothing rides; a change re-weighs the
    /// standing book (re-enabling continues the EMA from the carried last
    /// read, never a stale one).
    /// </summary>
    public bool ImbalanceSmoothed
    {
        get => _imbalanceSmoothed;
        set
        {
            this.RaiseAndSetIfChanged(ref _imbalanceSmoothed, value);
            RecomputeImbalance();
        }
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
        // chain keeps <see cref="Book"/> on the Avalonia UI thread — the same
        // hop re-weighs the T-246 imbalance chip, so the smoothing state is
        // UI-thread-confined too.
        Updates.ObserveOnUiThread()
            .Subscribe(
                book =>
                {
                    Book = book;
                    ApplyImbalance(book);
                },
                ex => _logger.LogError(ex, "The order book stream stopped unexpectedly"));

        // The chip resets on a symbol switch: the carried smoothed read
        // belongs to the old symbol's depth distribution — the new symbol's
        // first book passes through raw, and the chip blanks until it lands.
        this.WhenAnyValue(x => x.SymbolInput)
            .Skip(1)
            .Subscribe(
                _ => ResetImbalance(),
                ex => _logger.LogError(ex, "The imbalance symbol-reset stream stopped unexpectedly"));
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

    /// <summary>
    /// Re-weigh the snapshot (the pure calculator, allocation-free) and
    /// publish the smoothed read. Runs on the UI thread only.
    /// </summary>
    private void ApplyImbalance(OrderBook book)
    {
        var raw = BookImbalance.Ratio(book, _imbalanceWindow);
        _smoothedRead = BookImbalance.Smoothed(_smoothedRead, raw, _imbalanceSmoothed);
        PublishImbalance(_smoothedRead.Value);
    }

    /// <summary>
    /// Re-weigh the standing book after a knob change; before the first book
    /// lands there is nothing to weigh.
    /// </summary>
    private void RecomputeImbalance()
    {
        if (Book is { } book)
        {
            ApplyImbalance(book);
        }
    }

    /// <summary>Publish the read and its chip spelling (one lane, always together).</summary>
    private void PublishImbalance(decimal value)
    {
        Imbalance = value;
        ImbalanceText = FormatImbalance(value);
    }

    /// <summary>
    /// Blank the chip and drop the carried read (a symbol switch): the next
    /// symbol's first book passes through raw.
    /// </summary>
    private void ResetImbalance()
    {
        _smoothedRead = null;
        Imbalance = 0m;
        ImbalanceText = string.Empty;
    }

    /// <summary>
    /// The chip's invariant spelling: a signed two-decimal form, the sign
    /// never swallowed (the lean's direction is the read's whole point).
    /// </summary>
    private static string FormatImbalance(decimal value) =>
        value.ToString("+#0.00;-#0.00;0.00", CultureInfo.InvariantCulture);

    #endregion
}
