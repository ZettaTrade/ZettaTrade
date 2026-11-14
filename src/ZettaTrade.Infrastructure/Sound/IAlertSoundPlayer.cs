namespace ZettaTrade.Infrastructure.Sound;

/// <summary>
/// The alert-fired sound seam (T-096, T-220): the one dependency the alert
/// engine exercises to beep when an alert fires. The call returns PROMPTLY —
/// the implementation owns the playback's dispatch off the caller's path (a
/// firing burst never delays the evaluations behind it by the tone's own
/// duration), and it owns the degrade lane for the playback it dispatched:
/// a failed playback is a logged warning through the player's own lane,
/// never an unobserved fault, never a broken evaluation, never an app crash.
/// An implementation may still propagate a failing DISPATCH attempt as its
/// own exception — the CALLER (the engine's fire pipeline) keeps its degrade
/// catch for that.
/// </summary>
public interface IAlertSoundPlayer
{
    /// <summary>Dispatch the alert beep off the caller's path; the playback continues in the background and its faults log through the player's own lane.</summary>
    void Play();
}
