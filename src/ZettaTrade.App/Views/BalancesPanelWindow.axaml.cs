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
using ZettaTrade.App.ViewModels;

/// <summary>
/// Balances panel (per-exchange and aggregated). Owns the window plumbing:
/// the view model loads one aggregation snapshot once per activation
/// through the <see cref="BalancesPanelViewModel.LoadCommand"/>, the reset
/// flow's dialog handoff (<see cref="BalancesPanelViewModel.ResetRequested"/>,
/// T-192) is answered here — the reset dialog opens over this panel and its
/// parsed choice resolves the interaction — and the Close button closes the
/// window directly.
/// </summary>
public partial class BalancesPanelWindow : ReactiveWindow<BalancesPanelViewModel>
{
    /// <summary>
    /// Create a new balances panel window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public BalancesPanelWindow()
    {
        InitializeComponent();

        // Both registrations live in the collection-based activation scope:
        // the load pipeline runs once per activation and disposes with the
        // window, and so does the reset handoff handler registration — the
        // same scope the positions panel applies. The transient dialog view
        // model and its only window live exactly as long as the interaction
        // takes.
        this.WhenActivated(disposables =>
        {
            ViewModel?.LoadCommand
                .Execute()
                .Subscribe(
                    _ => { },
                    error => Log.Error(error, "Balances panel failed to load"))
                .DisposeWith(disposables);

            ViewModel?.ResetRequested
                .RegisterHandler(HandleResetRequestedAsync)
                .DisposeWith(disposables);
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Show the reset dialog over this panel and return the parsed starting
    /// balance; null when the dialog closes without one (user cancel, title
    /// bar X, or refused input kept the dialog open).
    /// </summary>
    private async Task<decimal?> ShowResetDialogAsync(decimal prefillBalance)
    {
        var viewModel = App.Services.GetRequiredService<PaperResetDialogViewModel>();
        viewModel.Initialize(prefillBalance);

        var dialog = new PaperResetDialogWindow
        {
            DataContext = viewModel,
        };
        await dialog.ShowDialog<object>(this);
        return dialog.ChosenBalance;
    }

    private async Task HandleResetRequestedAsync(IInteractionContext<decimal, decimal?> context)
    {
        try
        {
            context.SetOutput(await ShowResetDialogAsync(context.Input));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the paper reset dialog");
            context.SetOutput(null);
        }
    }
}
