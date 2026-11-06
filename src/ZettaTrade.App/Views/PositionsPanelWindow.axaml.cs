namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        return dialog.Result;
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
