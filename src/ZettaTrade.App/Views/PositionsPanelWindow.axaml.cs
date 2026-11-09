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
using ZettaTrade.Core.Entities;

/// <summary>
/// Positions and orders panel. Owns the window plumbing: the view model
/// reloads the sections once per activation through the
/// <see cref="PositionsPanelViewModel.LoadCommand"/>, and the amend flow's
/// dialog handoff (<see cref="PositionsPanelViewModel.AmendRequested"/>) is
/// answered here — the amend dialog window opens over this panel and its
/// parsed edit set resolves the interaction. The Close button closes the
/// window directly.
/// </summary>
public partial class PositionsPanelWindow : ReactiveWindow<PositionsPanelViewModel>
{
    /// <summary>
    /// Create a new positions panel window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public PositionsPanelWindow()
    {
        InitializeComponent();

        // Both registrations live in the collection-based activation scope:
        // the load pipeline runs once per activation and disposes with the
        // window, and so does the amend handoff handler registration — the
        // same scope the order dialog and the disclaimer dialog apply. The
        // transient dialog view model and its only window live exactly as
        // long as the interaction takes.
        this.WhenActivated(disposables =>
        {
            // The TESTNET lane badge (T-127): the panel mirrors the
            // status-bar chip — the badge appears only while the workspace's
            // traded surface rides the testnet lane. The lane stream
            // bootstraps with the standing value and re-resolves on surface
            // flips; the wiring dies with the window's disposal scope.
            var panelViewModel = ViewModel;
            if (panelViewModel is not null)
            {
                App.Services.GetRequiredService<TradingModeService>()
                    .Lane
                    .ObserveOnUiThread()
                    .Subscribe(
                        lane => panelViewModel.IsTestnetLane = lane == TradingLane.Testnet,
                        error => Log.Error(error, "Positions panel: the trading lane badge stream stopped unexpectedly"))
                    .DisposeWith(disposables);
            }

            ViewModel?.LoadCommand
                .Execute(RxVoid.Default)
                .Subscribe(
                    _ => { },
                    error => Log.Error(error, "Positions panel failed to load"))
                .DisposeWith(disposables);

            ViewModel?.AmendRequested
                .RegisterHandler(HandleAmendRequestedAsync)
                .DisposeWith(disposables);
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Show the amend dialog over this panel and return the parsed edit
    /// set; null when the dialog closes without one (user cancel, title
    /// bar X, or unparsable input kept the dialog open).
    /// </summary>
    private async Task<OrderAmendRequest?> ShowAmendDialogAsync(Order order)
    {
        var viewModel = App.Services.GetRequiredService<AmendOrderDialogViewModel>();
        viewModel.Initialize(order);

        var dialog = new AmendOrderDialogWindow
        {
            DataContext = viewModel,
        };
        await dialog.ShowDialog<object>(this);
        return dialog.AmendEdits;
    }

    private async Task HandleAmendRequestedAsync(IInteractionContext<Order, OrderAmendRequest?> context)
    {
        try
        {
            context.SetOutput(await ShowAmendDialogAsync(context.Input));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the amend order dialog");
            context.SetOutput(null);
        }
    }
}
