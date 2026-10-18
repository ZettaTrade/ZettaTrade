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
            // Handle symbol selection change
            this.WhenAnyValue(x => x.SelectedSymbol)
                .SelectMany(symbol => Observable.FromAsync(ct => LoadChartDataAsync(symbol, ct)))
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

    private async Task LoadSymbolsAsync(CancellationToken ct)
    {
        try
        {
            var symbols = await _marketData.GetSymbolsAsync(ct);
            AvailableSymbols = new ObservableCollection<Symbol>(symbols.Take(50)); // Limit for demo

            if (AvailableSymbols.Count > 0 && SelectedSymbol == null)
            {
                SelectedSymbol = AvailableSymbols[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load symbols");
        }
    }

    private async Task LoadChartDataAsync(Symbol? symbol, CancellationToken ct)
    {
        if (symbol is null)
            return;

        try
        {
            var bars = await _marketData.GetBarsAsync(
                symbol,
                Core.Enums.TimeFrame.H1,
                DateTimeOffset.UtcNow.AddDays(-7),
                limit: 200,
                ct: ct);

            Bars = new ObservableCollection<Bar>(bars);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chart data for {Symbol}", symbol);
        }
    }

    #endregion
}
