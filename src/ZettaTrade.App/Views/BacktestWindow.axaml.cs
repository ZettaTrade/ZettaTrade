namespace ZettaTrade.App.Views;

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;

/// <summary>
/// Backtest window (T-113): the run surface the backtest harness's
/// transient view model rides. Owns the window plumbing: the event and
/// language subscriptions attach on activation and dispose with it, so an
/// in-flight reference never outlives the window (the attach pattern).
/// </summary>
public partial class BacktestWindow : ReactiveWindow<ViewModels.BacktestRunViewModel>
{
    /// <summary>Create a new backtest window. The view model must be assigned before activation (the opener sets DataContext).</summary>
    public BacktestWindow()
    {
        InitializeComponent();

        // The transient view model's attach handle rides the activation
        // scope: the run-event, language and progress subscriptions die
        // with the window (the notification-center attach pattern).
        this.WhenActivated(disposables =>
        {
            ViewModel?.AttachRunEvents()
                .DisposeWith(disposables);
        });
    }

    /// <summary>The close button: the modal ends, the subscription scope dies with it.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
