namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Protective-levels dialog (T-187). Pure View: shows the stop-loss and
/// take-profit inputs over the open position, and hands the parsed
/// <see cref="ProtectiveLevelsEdits"/> back to the opener through
/// <see cref="ProtectEdits"/> (null when closed without an applied edit set
/// — title bar X, the Cancel button, or refused input kept the dialog
/// open). Button clicks stay in code-behind on purpose: closing a window is
/// View behavior, and the parse/direction gate is the dialog's only logic.
/// </summary>
public partial class ProtectiveLevelsDialogWindow : ReactiveWindow<ProtectiveLevelsDialogViewModel>
{
    /// <summary>
    /// Create a new ProtectiveLevelsDialogWindow instance. The view model
    /// must be assigned (and initialized) before activation: the opener sets
    /// <c>DataContext</c>.
    /// </summary>
    public ProtectiveLevelsDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>The parsed level edits, or null when closed without an applied input set.</summary>
    public ProtectiveLevelsEdits? ProtectEdits { get; private set; }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        // Refused input keeps the dialog open with the error line; a parsed
        // edit set closes and hands the levels to the opener.
        if (ViewModel?.TryBuildEdits() is { } edits)
        {
            ProtectEdits = edits;
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
