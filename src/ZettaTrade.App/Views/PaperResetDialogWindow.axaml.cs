namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Paper reset dialog (T-192). Pure View: shows the starting-balance input
/// over the scope summary, and hands the parsed choice back to the opener
/// through <see cref="ChosenBalance"/> (null when closed without an applied
/// choice — title bar X, the Cancel button, or refused input kept the
/// dialog open). Button clicks stay in code-behind on purpose: closing a
/// window is View behavior, and the parse gate is the dialog's only logic.
/// </summary>
public partial class PaperResetDialogWindow : ReactiveWindow<PaperResetDialogViewModel>
{
    /// <summary>
    /// Create a new PaperResetDialogWindow instance. The view model must be
    /// assigned (and initialized) before activation: the opener sets
    /// <c>DataContext</c>.
    /// </summary>
    public PaperResetDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>The parsed starting balance, or null when closed without an applied choice.</summary>
    public decimal? ChosenBalance { get; private set; }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        // Refused input keeps the dialog open with the error line; a parsed
        // choice closes and hands the balance to the opener.
        if (ViewModel is { } viewModel && viewModel.TryBuildChoice())
        {
            ChosenBalance = viewModel.ChosenBalance;
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
