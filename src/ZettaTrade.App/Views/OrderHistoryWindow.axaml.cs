namespace ZettaTrade.App.Views;

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Order history window. Owns the window plumbing: the view model loads the
/// persisted rows once per activation through the
/// <see cref="OrderHistoryViewModel.LoadCommand"/>; the Close button closes
/// the window directly.
/// </summary>
public partial class OrderHistoryWindow : ReactiveWindow<OrderHistoryViewModel>
{
    /// <summary>
    /// Create the order history window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public OrderHistoryWindow()
    {
        InitializeComponent();

        // The explicit Action<Action<IDisposable>> delegate removes the
        // WhenActivated overload ambiguity for a body that registers no
        // disposables: the load pipeline runs once per activation and
        // self-terminates, with errors caught in the Subscribe call.
        this.WhenActivated(new Action<Action<IDisposable>>(disposables =>
        {
            ViewModel?.LoadCommand
                .Execute()
                .Subscribe(
                    _ => { },
                    error => Log.Error(error, "Order history window failed to load"));
        }));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
