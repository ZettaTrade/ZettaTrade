namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Extensions;

using Observable = System.Reactive.Linq.Observable;

/// <summary>
/// ViewModel for chart display.
/// </summary>
public sealed class ChartViewModel : ReactiveObject, IActivatableViewModel
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

    public ViewModelActivator Activator { get; } = new();

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

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new ChartViewModel instance.
    /// </summary>
    public ChartViewModel(
        IMarketDataAdapter marketData,
        ILogger<ChartViewModel> logger)
    {
        _marketData = marketData;
        _logger = logger;

        this.WhenActivated(disposables =>
        {
            // Redraw the chart when the selected symbol changes. Switch() keeps only the
            // latest load running and cancels the previous one, so bars never arrive out of order.
            this.WhenAnyValue(x => x.SelectedSymbol)
                .Select(symbol => Observable.FromAsync(token => LoadChartDataAsync(symbol, token)))
                .Switch()
                .ObserveOnUiThread()
                .Subscribe(
                    _ => { },
                    ex => _logger.LogError(ex, "Failed to update chart data for selected symbol"))
                .DisposeWith(disposables);

            // Load initial symbols list
            Observable.FromAsync(ct => LoadSymbolsAsync(ct))
                .ObserveOnUiThread()
                .Subscribe(
                    _ => { },
                    ex => _logger.LogError(ex, "Failed to load symbols"))
                .DisposeWith(disposables);
        });
    }

    #endregion

    #region Private Methods

    private async Task<int> LoadSymbolsAsync(CancellationToken ct)
    {
        try
        {
            var symbols = await _marketData.GetSymbolsAsync(ct);
            var visibleSymbols = symbols.Take(MaxVisibleSymbols).ToList();
            AvailableSymbols = new ObservableCollection<Symbol>(visibleSymbols);

            if (AvailableSymbols.Count > 0 && SelectedSymbol == null)
            {
                SelectedSymbol = AvailableSymbols[0];
            }

            // Task<int> (not plain Task) keeps Rx pipelines on ReactiveUI.RxVoid; System.Reactive.Unit is banned.
            return AvailableSymbols.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load symbols");
            return 0;
        }
    }

    private async Task<int> LoadChartDataAsync(Symbol? symbol, CancellationToken ct)
    {
        if (symbol is null)
            return 0;

        try
        {
            var bars = await _marketData.GetBarsAsync(
                symbol,
                ChartTimeFrame,
                DateTimeOffset.UtcNow.AddDays(-ChartLookbackDays),
                limit: InitialBarCount,
                ct: ct);

            Bars = new ObservableCollection<Bar>(bars);

            // Task<int> (not plain Task) keeps Rx pipelines on ReactiveUI.RxVoid; System.Reactive.Unit is banned.
            return bars.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chart data for {Symbol}", symbol);
            return 0;
        }
    }

    #endregion
}
