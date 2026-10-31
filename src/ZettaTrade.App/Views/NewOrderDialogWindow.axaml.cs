namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
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
            ViewModel?.CloseRequested
                .RegisterHandler(context =>
                {
                    context.SetOutput(RxVoid.Default);
                    Close();
                    return Task.CompletedTask;
                })
                .DisposeWith(disposables);
        });
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
