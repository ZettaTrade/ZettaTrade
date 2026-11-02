namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;
using ZettaTrade.UI.Extensions;

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
            // The autocandidate list loads on activation and dies with the
            // window: the transient view model and its only window live
            // exactly as long as this disposal scope.
            ViewModel?.LoadSymbolsCommand
                .Execute(RxVoid.Default)
                .Subscribe(
                    _ => { },
                    ex => Log.Error(ex, "Order dialog: symbol list load failed to start"))
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
