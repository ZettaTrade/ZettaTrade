namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
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
/// <see cref="Bars"/> is assigned after the UI-thread hop.
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
    private readonly IMarketDataAdapter _marketData;

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
        IMarketDataAdapter marketData,
        ILogger<ChartViewModel> logger)
    {
        _marketData = marketData;
        _logger = logger;

        SymbolSelections = this.WhenAnyValue(x => x.SelectedSymbol)
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!);

        BarsUpdates = SymbolSelections
            .Select(LoadBars)
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

        // Bootstrap for the nested-panel singleton: load the symbol list once
        // (assignments are marshaled; the failure stays logged and leaves the
        // combo empty), then seed the combo with its first entry — the single
        // workspace default owner.
        Observable.FromAsync(LoadSymbolsAsync)
            .ObserveOnUiThread()
            .Subscribe(
                symbols =>
                {
                    AvailableSymbols = new ObservableCollection<Symbol>(symbols);

                    if (SelectedSymbol is null && AvailableSymbols.Count > 0)
                    {
                        SelectedSymbol = AvailableSymbols[0];
                        _logger.LogInformation(
                            "Symbols loaded: {Count}; preselected {Symbol}",
                            AvailableSymbols.Count,
                            SelectedSymbol);
                    }
                },
                ex => _logger.LogError(ex, "Failed to load symbols"));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Fetch the symbol list, trimmed to the combo capacity. Failures belong
    /// to the subscribe side (logged there, the combo stays empty).
    /// </summary>
    private async Task<IReadOnlyList<Symbol>> LoadSymbolsAsync(CancellationToken ct)
    {
        var symbols = await _marketData.GetSymbolsAsync(ct);
        return symbols.Take(MaxVisibleSymbols).ToList();
    }

    /// <summary>
    /// Bars load for one selection. A failure ends this selection's data
    /// (logged, never a default value) but must not terminate the switching
    /// machine for later selections.
    /// </summary>
    private IObservable<IReadOnlyList<Bar>> LoadBars(Symbol symbol)
    {
        _logger.LogInformation("Chart bars load requested for {Symbol}", symbol);

        return Observable
            .FromAsync(token => _marketData.GetBarsAsync(
                symbol,
                ChartTimeFrame,
                DateTimeOffset.UtcNow.AddDays(-ChartLookbackDays),
                limit: InitialBarCount,
                ct: token))
            // A cancelled load is the normal outcome of Switch dropping the
            // previous selection: keep it silent (real failures log below).
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
