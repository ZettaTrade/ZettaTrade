namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Amend order dialog. Pure View: shows the amend inputs over the working
/// order, and hands the parsed <see cref="OrderAmendRequest"/> back to the
/// opener through <see cref="Result"/> (null when closed without an
/// explicit amend — title bar X, the Cancel button, or unparsable input).
/// Button clicks stay in code-behind on purpose: closing a window is View
/// behavior, and the parse gate is the dialog's only logic.
/// </summary>
public partial class AmendOrderDialogWindow : ReactiveWindow<AmendOrderDialogViewModel>
{
    /// <summary>
    /// Create a new AmendOrderDialogWindow instance. The view model must be
    /// assigned (and initialized) before activation: the opener sets
    /// <c>DataContext</c>.
    /// </summary>
    public AmendOrderDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>The parsed amend request, or null when closed without an amendable input set.</summary>
    public OrderAmendRequest? Result { get; private set; }

    private void OnAmendClick(object? sender, RoutedEventArgs e)
    {
        // Unparsable input keeps the dialog open with the error line; a
        // parsed edit set closes and hands the request to the opener.
        if (ViewModel?.TryBuildEdits() is { } edits)
        {
            Result = edits;
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
