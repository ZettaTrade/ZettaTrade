namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// New Order dialog. Owns the window plumbing: the view model's
/// <see cref="NewOrderDialogViewModel.CloseRequested"/> interaction closes
/// the window (order placed or aborted), the Cancel button aborts directly.
/// </summary>
public partial class NewOrderDialogWindow : ReactiveWindow<NewOrderDialogViewModel>
{
    /// <summary>
    /// Create a new New Order dialog window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public NewOrderDialogWindow()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            // The TESTNET lane badge (T-127): the ticket mirrors the
            // status-bar chip — the badge appears only while the workspace's
            // traded surface rides the testnet lane. The lane stream
            // bootstraps with the standing value and re-resolves on surface
            // flips; the wiring dies with the window's disposal scope.
            var dialogViewModel = ViewModel;
            if (dialogViewModel is not null)
            {
                App.Services.GetRequiredService<TradingModeService>()
                    .Lane
                    .ObserveOnUiThread()
                    .Subscribe(
                        lane => dialogViewModel.IsTestnetLane = lane == TradingLane.Testnet,
                        ex => Log.Error(ex, "Order dialog: the trading lane badge stream stopped unexpectedly"))
                    .DisposeWith(disposables);
            }

            // The autocandidate list loads on activation and dies with the
            // window: the transient view model and its only window live
            // exactly as long as this disposal scope.
            ViewModel?.LoadSymbolsCommand
                .Execute(RxVoid.Default)
                .Subscribe(
                    _ => { },
                    ex => Log.Error(ex, "Order dialog: symbol list load failed to start"))
                .DisposeWith(disposables);

            // The quick-size row's stored default loads once per open, the
            // same transient lifetime; the snapshots ride the symbol
            // commits, never the sizing itself.
            ViewModel?.InitializeSizingCommand
                .Execute(RxVoid.Default)
                .Subscribe(
                    _ => { },
                    ex => Log.Error(ex, "Order dialog: sizing default load failed to start"))
                .DisposeWith(disposables);

            ViewModel?.CloseRequested
                .RegisterHandler(context =>
                {
                    context.SetOutput(RxVoid.Default);
                    Close();
                    return Task.CompletedTask;
                })
                .DisposeWith(disposables);
        });

        // The transient view model owns subscriptions of its own (the
        // active-exchange header stream); they release with the window, the
        // same moment this activation disposal scope dies.
        Closed += (_, _) => ViewModel?.Dispose();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
