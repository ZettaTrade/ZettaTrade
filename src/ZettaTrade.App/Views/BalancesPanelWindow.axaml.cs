namespace ZettaTrade.App.Views;

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Balances panel (per-exchange and aggregated). Owns the window plumbing:
/// the view model loads one aggregation snapshot once per activation
/// through the <see cref="BalancesPanelViewModel.LoadCommand"/>; the Close
/// button closes the window directly.
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
                    error => Log.Error(error, "Balances panel failed to load"));
        }));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
