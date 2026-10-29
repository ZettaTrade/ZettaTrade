namespace ZettaTrade.App.ViewModels;

using ReactiveUI;

/// <summary>
/// ViewModel for the modal confirmation dialog.
/// Holds the composed message only; the dialog outcome is window plumbing
/// owned by the <see cref="Views.ConfirmationDialogWindow"/>, so this VM
/// stays free of any View types.
/// </summary>
public sealed class ConfirmationDialogViewModel : ReactiveObject
{
    /// <summary>
    /// Create a new ConfirmationDialogViewModel instance.
    /// </summary>
    public ConfirmationDialogViewModel(string message)
    {
        Message = message;
    }

    /// <summary>Confirmation text, already resolved in the user's language by the caller.</summary>
    public string Message { get; }
}
