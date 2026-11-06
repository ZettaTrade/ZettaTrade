namespace ZettaTrade.App.Views;

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Trade statistics window. Owns the window plumbing: the view model starts
/// its paged scan once per window instance (a re-activation refocus must not
/// restart a running scan); the Close button closes the window directly.
/// Disposing the execution subscription on window close cancels an in-flight
/// scan.
/// </summary>
public partial class TradeStatsWindow : ReactiveWindow<TradeStatsViewModel>
{
    /// <summary>Whether the one-shot activation load already ran for this window instance.</summary>
    private bool _initialLoadQueued;

    /// <summary>
    /// Create a new trade statistics window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public TradeStatsWindow()
    {
        InitializeComponent();

        // The explicit Action<Action<IDisposable>> delegate removes the
        // WhenActivated overload ambiguity (the positions-panel pattern).
        // The load runs once per window and errors land in the Subscribe
        // callback; the disposal registration cancels the scan on close.
        this.WhenActivated(new Action<Action<IDisposable>>(disposables =>
        {
            if (_initialLoadQueued || ViewModel is null)
            {
                return;
            }

            _initialLoadQueued = true;

            disposables(ViewModel.ApplyCommand.Execute().Subscribe(
                _ => { },
                error => Log.Error(error, "Trade statistics scan failed")));
        }));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
