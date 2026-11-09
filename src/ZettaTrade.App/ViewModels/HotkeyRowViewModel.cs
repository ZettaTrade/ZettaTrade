namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ZettaTrade.App.Services;

/// <summary>
/// One editable hotkey row of the settings tab (T-102): the registry
/// action, the gesture text as typed, and the edit-time verdict (a
/// conflict or an invalid form) the parent ViewModel diagnoses. The row
/// is dumb state — every verdict and every persistence rides
/// <see cref="HotkeyViewModel"/>.
/// </summary>
public sealed class HotkeyRowViewModel : ReactiveObject
{
    private string _gestureText;

    private string _message = string.Empty;

    private bool _isOverridden;

    private string _name;

    /// <summary>
    /// Create a new HotkeyRowViewModel instance.
    /// </summary>
    /// <param name="action">The registry action the row edits.</param>
    /// <param name="gestureText">The row's starting (effective) gesture text.</param>
    /// <param name="name">The localized display name (the tab's row label).</param>
    public HotkeyRowViewModel(HotkeyAction action, string gestureText, string name)
    {
        Action = action;
        _gestureText = gestureText;
        _name = name;
    }

    /// <summary>Gets the registry action the row edits.</summary>
    public HotkeyAction Action { get; }

    /// <summary>Gets the localized display name (re-localized by the parent on a language switch).</summary>
    public string Name => _name;

    /// <summary>Set the row's localized display name (the parent's re-localization pass).</summary>
    /// <param name="name">The localized display name.</param>
    public void SetName(string name) =>
        this.RaiseAndSetIfChanged(ref _name, name, nameof(Name));

    /// <summary>Gets or sets the gesture text as typed (the edit-time verdict re-runs on every change).</summary>
    public string GestureText
    {
        get => _gestureText;
        set => this.RaiseAndSetIfChanged(ref _gestureText, value);
    }

    /// <summary>Gets the edit-time verdict (localized; empty when the row is valid).</summary>
    public string Message => _message;

    /// <summary>Gets a value indicating whether the row carries a verdict to show.</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>Gets or sets a value indicating whether the action carries a user override (the default marker's mirror).</summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        set => this.RaiseAndSetIfChanged(ref _isOverridden, value);
    }

    /// <summary>Set the row's verdict (the parent's diagnosis; empty clears).</summary>
    /// <param name="message">The localized verdict text, or empty.</param>
    public void SetMessage(string message)
    {
        this.RaiseAndSetIfChanged(ref _message, message, nameof(Message));
        this.RaisePropertyChanged(nameof(HasMessage));
    }
}
