using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Modal confirmation dialog window. Pure View: binds the message from
/// <see cref="ConfirmationDialogViewModel"/>, records the user's answer.
/// Button clicks stay in code-behind on purpose — closing a window is View
/// behavior, and routing it through the ViewModel would leak Window types into it.
/// </summary>
public partial class ConfirmationDialogWindow : ReactiveWindow<ConfirmationDialogViewModel>
{
    /// <summary>
    /// Create a new ConfirmationDialogWindow instance.
    /// </summary>
    public ConfirmationDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>User's answer, or null when closed without an explicit choice (title bar X, timeout).</summary>
    public bool? Result { get; private set; }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
