namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for chart display.
/// </summary>
public sealed class ChartViewModel : ReactiveObject, IActivatableViewModel
{
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
            // Load available symbols
            LoadSymbolsAsync()
                .Subscribe(
                    _ => { },
                    ex => _logger.LogError(ex, "Failed to load symbols"));
        });
    }

    #endregion

    #region Private Methods

    private async Task LoadSymbolsAsync()
    {
        try
        {
            var symbols = await _marketData.GetSymbolsAsync();
            AvailableSymbols = new ObservableCollection<Symbol>(symbols.Take(50)); // Limit for demo
            
            if (AvailableSymbols.Count > 0)
            {
                SelectedSymbol = AvailableSymbols[0];
                await LoadChartDataAsync(SelectedSymbol);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load symbols");
        }
    }

    private async Task LoadChartDataAsync(Symbol symbol)
    {
        try
        {
            var bars = await _marketData.GetBarsAsync(
                symbol,
                Core.Enums.TimeFrame.H1,
                DateTimeOffset.UtcNow.AddDays(-7),
                limit: 200);

            Bars = new ObservableCollection<Bar>(bars);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chart data for {Symbol}", symbol);
        }
    }

    #endregion
}
