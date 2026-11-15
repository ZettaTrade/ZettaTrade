namespace ZettaTrade.App.ViewModels;

using System;
using System.Linq;
using System.Reactive.Linq;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Liquidation tape panel state (T-242): a typed symbol input (free-form,
/// the trade tape's pattern, so the panel still works offline), the live
/// forced-liquidation stream for the parsed symbol, the pulse-paced window
/// state (the open cascade the widget badges) and the active adapter's
/// capability flag (an adapter without the venue's forced-order wire renders
/// the honest unavailable state, never a dead panel waiting for data).
/// </summary>
/// <remarks>
/// Like <see cref="TradeTapeViewModel"/>, this is an application-lifetime
/// singleton whose subscriptions are created in the constructor: WhenActivated
/// never fires for VMs bound as nested-panel DataContexts, and the panel must
/// stream from the first frame. The workspace selection (the chart's symbol
/// combo) is the owner and rewrites the typed input; the typed input stays
/// the free-form override and the last user action wins.
/// <see cref="Updates"/> stays raw (testable without the UI thread): the
/// widget consumes the stream itself (buffering, coalescing, pausing — its
/// own frame discipline). The window state is pulse-paced on the feed's lane
/// and marshalled onto the UI thread before the property writes.
/// </remarks>
public sealed class LiquidationsViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Symbol typed at start, so the panel streams live data immediately.</summary>
    public const string DefaultSymbolInput = "BTC/USDT";

    #endregion

    #region Private Fields

    private readonly LiquidationFeedService _feed;
    private readonly ILogger<LiquidationsViewModel> _logger;

    #endregion

    #region Properties

    private string _symbolInput = DefaultSymbolInput;

    /// <summary>Raw symbol text; a parse failure keeps the previous rows visible.</summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set => this.RaiseAndSetIfChanged(ref _symbolInput, value);
    }

    /// <summary>
    /// Live forced-liquidation events for the parsed symbol, switched so a new
    /// symbol cancels the previous stream. Raw — not UI-marshaled; the widget
    /// owns its own buffering and frame discipline.
    /// </summary>
    public IObservable<LiquidationEvent> Updates { get; }

    private LiquidationsWindowState _window = new([], 0, 0m, null);

    /// <summary>
    /// The symbol's aggregated window state (the feed's latest pulse): the
    /// recent events' rolled-up totals and the open cascade the widget
    /// badges. An honest no-cascade state renders no badge.
    /// </summary>
    public LiquidationsWindowState Window
    {
        get => _window;
        private set => this.RaiseAndSetIfChanged(ref _window, value);
    }

    private bool _isAvailable;

    /// <summary>
    /// Whether the active adapter publishes forced-liquidation events (the
    /// capability probe's current answer). False renders the panel's
    /// unavailable note — the venue's honest absence.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isAvailable, value);
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new liquidation tape ViewModel instance over the feed and the
    /// workspace symbol chain. The subscriptions mirror the application
    /// lifetime (the workspace-panel convention).
    /// </summary>
    public LiquidationsViewModel(
        LiquidationFeedService feed,
        IObservable<Symbol> workspaceSelections,
        ILogger<LiquidationsViewModel> logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var symbolChanges = this.WhenAnyValue(x => x.SymbolInput)
            .Select(ToSymbol)
            .DistinctUntilChanged()
            .Where(symbol => symbol is not null);

        Updates = symbolChanges
            .Select(symbol => GuardStreamFailures(_feed.SubscribeEvents(symbol!)))
            .Switch();

        symbolChanges
            .Select(symbol => GuardStreamFailures(_feed.SubscribeWindow(symbol!)))
            .Switch()
            .ObserveOnUiThread()
            .Subscribe(
                ApplyWindow,
                ex => _logger.LogError(ex, "The liquidation window stream stopped unexpectedly"));

        _feed.Availability
            .ObserveOnUiThread()
            .Subscribe(
                available => IsAvailable = available,
                ex => _logger.LogError(ex, "The liquidation capability stream stopped unexpectedly"));

        // The workspace combo owns the symbol: a selection rewrites the typed
        // input (RaiseAndSetIfChanged dedupes the no-change repeats) and the
        // streams' switch chains follow. The ViewModel lives as long as the
        // application (singleton), so this subscription mirrors it.
        workspaceSelections.Subscribe(
            symbol =>
            {
                SymbolInput = symbol.Normalized;
                _logger.LogInformation("Workspace selection applied to the liquidation panel: {Symbol}", symbol.Normalized);
            },
            ex => _logger.LogError(ex, "The liquidation panel's workspace symbol stream stopped unexpectedly"));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Apply one window state (the pulse's aggregated page): the property
    /// write the badge binding reads. The state is immutable, so the swap is
    /// one reference write.
    /// </summary>
    private void ApplyWindow(LiquidationsWindowState state) => Window = state;

    /// <summary>
    /// Parse typed input into a normalized symbol; unexpected input yields
    /// null, which switches to no data (the widget keeps what it already
    /// has).
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
    private IObservable<T> GuardStreamFailures<T>(IObservable<T> source) =>
        source.Catch<T, Exception>(ex =>
        {
            _logger.LogError(ex, "A liquidation stream ended with an error");
            return Observable.Empty<T>();
        });

    #endregion
}
