namespace ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The exit codes a second (redirected) start returns to the shell. The
/// redirect contract is exit-parity: every second start leaves through the
/// same prompt-exit lane, whatever the outcome, and the code tells the
/// integration what happened — 0 the forwarding was accepted by the running
/// instance, 1 the argument vector was rejected by the allowlist grammar,
/// 2 no running instance could be reached in the bounded forward window.
/// </summary>
public enum LaunchExitCode
{
    /// <summary>The running instance forwarded and acknowledged the start.</summary>
    Forwarded = 0,

    /// <summary>
    /// The argument vector carried a token outside the handoff allowlist
    /// (an unknown — including any order-shaped — flag, a missing or empty
    /// value, a duplicate flag or an unnoticed protocol string). Nothing
    /// was forwarded; nothing on the running instance changed.
    /// </summary>
    ArgsRejected = 1,

    /// <summary>
    /// A running instance holds the dispatch claim, but the handoff pipe
    /// never served a forward inside the bounded window (it is still
    /// booting, wedged, or its pipe died after the claim). The start exits
    /// promptly instead of waiting the app out.
    /// </summary>
    HandoffUnavailable = 2,
}
