namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Extensions;

/// <summary>
/// Chart panel state: the available symbols list, the selected symbol and the
/// loaded bars. Application-lifetime singleton: the pipelines are built in the
/// constructor — WhenActivated never fires for VMs bound as nested-panel
/// DataContexts (the documented workspace fact), so there is no activation path.
/// </summary>
/// <remarks>
/// The chart owns the workspace symbol context: <see cref="SymbolSelections"/>
/// publishes every confirmed selection (the workspace combo change and the
/// symbols preselect both raise <see cref="SelectedSymbol"/>) for watchers such
/// as the order book panel. <see cref="BarsUpdates"/> stays raw and testable;
/// <see cref="Bars"/> is assigned after the UI-thread hop. The exchange
/// context is inherited from the active-exchange facade: both the symbol
/// bootstrap and the bars machine re-run on every adapter change, so the
/// workspace follows the toolbar selector blind. The ViewModel lives as
/// long as the application (singleton), so the subscriptions mirror it
/// and need no explicit disposal.
/// </remarks>
public sealed class ChartViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Upper bound of the symbols dropdown in the demo view.</summary>
    private const int MaxVisibleSymbols = 50;

    /// <summary>Historical window length for chart bars, in days.</summary>
    private const int ChartLookbackDays = 7;

    /// <summary>Initial number of candles loaded for the chart.</summary>
    private const int InitialBarCount = 200;

    /// <summary>Chart timeframe while the timeframe picker is not implemented.</summary>
    private const Core.Enums.TimeFrame ChartTimeFrame = Core.Enums.TimeFrame.H1;

    #endregion

    #region Properties

    private readonly ILogger<ChartViewModel> _logger;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolListProvider _symbolList;

    private ObservableCollection<Bar> _bars = new();

    /// <summary>Collection of bars to display in the chart.</summary>
    public ObservableCollection<Bar> Bars
    {
        get => _bars;
        private set => this.RaiseAndSetIfChanged(ref _bars, value);
    }

    private Symbol? _selectedSymbol;

    /// <summary>Currently selected trading symbol.</summary>
    public Symbol? SelectedSymbol
    {
        get => _selectedSymbol;
        set => this.RaiseAndSetIfChanged(ref _selectedSymbol, value);
    }

    private ObservableCollection<Symbol> _availableSymbols = new();

    /// <summary>List of available trading symbols.</summary>
    public ObservableCollection<Symbol> AvailableSymbols
    {
        get => _availableSymbols;
        private set => this.RaiseAndSetIfChanged(ref _availableSymbols, value);
    }

    /// <summary>
    /// Every confirmed symbol selection: the workspace combo and the symbols
    /// preselect both flow through here. Raw — not UI-marshaled.
    /// </summary>
    public IObservable<Symbol> SymbolSelections { get; }

    /// <summary>
    /// Bars loaded for the latest selection; a newer selection cancels the
    /// previous load (Rx Switch). The machine is hot and shared: exactly one
    /// load pipeline runs no matter how many consumers subscribe, and a late
    /// watcher immediately gets the latest bars. Raw — not UI-marshaled;
    /// user-interface consumers marshal themselves.
    /// </summary>
    public IObservable<IReadOnlyList<Bar>> BarsUpdates { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new ChartViewModel instance.
    /// </summary>
    public ChartViewModel(
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        ILogger<ChartViewModel> logger)
    {
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _logger = logger;

        SymbolSelections = this.WhenAnyValue(x => x.SelectedSymbol)
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!);

        // Bars re-load on every selection or active-exchange change: the pair
        // pins the adapter that owns the bars, so each load targets exactly
        // the exchange of its triggering change, and the Switch drops the
        // in-flight load of a superseded pair (atomic by construction).
        BarsUpdates = SymbolSelections
            .CombineLatest(activeExchanges.AdapterChanges, (symbol, adapter) => (Symbol: symbol, Adapter: adapter))
            .Select(pair => LoadBars(pair.Symbol, pair.Adapter))
            .Switch()
            .Replay(1)
            .RefCount();

        // The ViewModel lives as long as the application (singleton), so this
        // subscription mirrors it and needs no explicit disposal. The marshal
        // chain keeps <see cref="Bars"/> on the Avalonia UI thread.
        BarsUpdates.ObserveOnUiThread()
            .Subscribe(
                bars =>
                {
                    Bars = new ObservableCollection<Bar>(bars);
                    _logger.LogInformation("Chart bars delivered: {Count} candles", bars.Count);
                },
                ex => _logger.LogError(ex, "The chart bars stream stopped unexpectedly"));

        // The symbol list belongs to the exchange: the bootstrap re-runs on
        // every adapter change (the behavioral stream also performs the first
        // load right at construction) and keeps the previous selection alive
        // while the new exchange's list still carries it.
        activeExchanges
            .AdapterChanges
            .Subscribe(
                _ => BootstrapSymbols(),
                ex => _logger.LogError(ex, "The chart symbols stream stopped unexpectedly"));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Fetch the symbol list through the shared
    /// <see cref="SymbolListProvider"/> (one <c>exchangeInfo</c> fetch per
    /// application run per exchange serves the chart combo and the order
    /// dialog autocomplete alike; the provider is warmed at startup and
    /// flushed on every exchange switch, so this bootstrap usually awaits a
    /// fetch or a ready cache), trimmed to the combo capacity. Failures
    /// belong to the subscribe side (logged there, the combo stays empty).
    /// </summary>
    private async Task<IReadOnlyList<Symbol>> LoadSymbolsAsync(CancellationToken ct)
    {
        var names = await _symbolList.GetSymbolsAsync(ct);
        return names.Select(Symbol.Parse).Take(MaxVisibleSymbols).ToList();
    }

    /// <summary>
    /// Load the symbol list for the current exchange and preselect. The
    /// selection decision is computed raw (pinned by tests through
    /// <see cref="Preselect"/>); the marshaled subscribe side only applies
    /// the assignments, and a failure stays logged, leaving the combo empty.
    /// </summary>
    private void BootstrapSymbols()
    {
        Observable.FromAsync(LoadSymbolsAsync)
            .Select(symbols => (
                Symbols: symbols,
                Selection: Preselect(SelectedSymbol, symbols)))
            .ObserveOnUiThread()
            .Subscribe(
                page =>
                {
                    AvailableSymbols = new ObservableCollection<Symbol>(page.Symbols);
                    SelectedSymbol = page.Selection;
                    _logger.LogInformation(
                        "Symbols loaded: {Count}; selected {Symbol}",
                        AvailableSymbols.Count,
                        SelectedSymbol);
                },
                ex => _logger.LogError(ex, "Failed to load symbols"));
    }

    /// <summary>
    /// The preselection decision, in its pure form (pinned directly by
    /// tests): the current selection survives while the new list carries
    /// it, otherwise the first entry owns the workspace default; an empty
    /// list clears the selection with no substitute.
    /// </summary>
    internal static Symbol? Preselect(Symbol? currentSelection, IReadOnlyList<Symbol> symbols) =>
        currentSelection is { } kept && symbols.Contains(kept)
            ? kept
            : symbols.FirstOrDefault();

    /// <summary>
    /// Bars load for one selection on the exchange that triggered it. A
    /// failure ends this selection's data (logged, never a default value)
    /// but must not terminate the switching machine for later selections.
    /// </summary>
    private IObservable<IReadOnlyList<Bar>> LoadBars(Symbol symbol, IMarketDataAdapter adapter)
    {
        _logger.LogInformation("Chart bars load requested for {Symbol} on {Exchange}", symbol, adapter.Name);

        return Observable
            .FromAsync(token => adapter.GetBarsAsync(
                symbol,
                ChartTimeFrame,
                DateTimeOffset.UtcNow.AddDays(-ChartLookbackDays),
                limit: InitialBarCount,
                ct: token))
            // A cancelled load is the normal outcome of Switch dropping the
            // previous selection or exchange: keep it silent (real failures
            // log below).
            .Catch<IReadOnlyList<Bar>, OperationCanceledException>(_ =>
                Observable.Empty<IReadOnlyList<Bar>>())
            .Catch<IReadOnlyList<Bar>, Exception>(ex =>
            {
                _logger.LogError(ex, "Failed to load chart data for {Symbol}", symbol);
                return Observable.Empty<IReadOnlyList<Bar>>();
            });
    }

    #endregion
}
