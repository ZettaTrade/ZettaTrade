namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ZettaTrade.App.Services;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// One per-source mute toggle row (T-097): the localized source label and
/// the muted flag the center's settings stream keeps honest. The toggle
/// command is built by the history window's view model (it captures the
/// row's kind and writes the settings service through the reactive keys).
/// </summary>
public sealed class NotificationMuteRow : ReactiveObject
{
    #region Private Fields

    private bool _isMuted;

    #endregion Private Fields

    /// <summary>The notification kind the row mutes.</summary>
    public NotificationKind Kind { get; }

    /// <summary>The composed localized source label (dynamic data — never a static localized string).</summary>
    public string Label { get; }

    /// <summary>Whether the source's toasts are muted right now (the settings stream is the writer).</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }

    /// <summary>Flip the row's mute: the command writes the persisted flip; the change stream re-applies the set.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    /// <summary>Create one mute row.</summary>
    /// <param name="kind">The kind the row mutes.</param>
    /// <param name="label">The composed localized source label.</param>
    /// <param name="toggleCommand">The flip command the history window's view model built.</param>
    public NotificationMuteRow(
        NotificationKind kind,
        string label,
        ReactiveCommand<RxVoid, RxVoid> toggleCommand)
    {
        Kind = kind;
        Label = label;
        ToggleCommand = toggleCommand ?? throw new ArgumentNullException(nameof(toggleCommand));
    }
}
