namespace ZettaTrade.App.Views;

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;

/// <summary>
/// Alerts window (T-096): the manager surface the alerts module's transient
/// view model rides. Owns the window plumbing: the engine-event and language
/// subscriptions attach on activation and dispose with it, so the singleton
/// engine never retains a dead window's reference (the attach pattern).
/// </summary>
public partial class AlertsWindow : ReactiveWindow<ViewModels.AlertsViewModel>
{
    /// <summary>Create a new alerts window. The view model must be assigned before activation (the opener sets DataContext).</summary>
    public AlertsWindow()
    {
        InitializeComponent();

        // The transient view model's attach handle rides the activation
        // scope: the engine-event and language subscriptions die with the
        // window (the notification-center attach pattern).
        this.WhenActivated(disposables =>
        {
            ViewModel?.Attach()
                .DisposeWith(disposables);
        });
    }

    /// <summary>The close button: the modal ends, the subscription scope dies with it.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
