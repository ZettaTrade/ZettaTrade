namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Notification center window (T-097). Owns the window plumbing: the
/// transient view model attaches the live event lane and seeds the session
/// history snapshot once per activation — the attach runs first (the seed
/// read follows, the reference-identity guard keeps the crossing publishes
/// from double rendering) — and the lane stopper dies with the activation
/// scope (the transient view model and its only window share their
/// lifetime). The DND and mute toggles land in the ViewModel's commands;
/// the Close button closes the window directly.
/// </summary>
public partial class NotificationHistoryWindow : ReactiveWindow<NotificationHistoryViewModel>
{
    /// <summary>
    /// Create the notification center window. The view model must be
    /// assigned before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public NotificationHistoryWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // The live lane's subscription dies with the window; the seed
            // runs once per activation right behind the attach (the guard
            // inside the view model deduplicates the crossing publishes).
            ViewModel?.AttachLiveHistory()
                .DisposeWith(disposables);
            ViewModel?.LoadHistory();
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
