namespace ZettaTrade.Infrastructure.Sound;

/// <summary>
/// The alert-fired sound seam (T-096): the one dependency the alert engine
/// exercises to beep when an alert fires. An implementation may propagate a
/// failing platform attempt as its own exception — the CALLER (the engine's
/// fire pipeline) owns the degrade contract: a failed beep is a logged
/// warning, never a broken evaluation, never an app crash.
/// </summary>
public interface IAlertSoundPlayer
{
    /// <summary>Play the alert beep once; a failing platform attempt throws for the caller to degrade on.</summary>
    void Play();
}
