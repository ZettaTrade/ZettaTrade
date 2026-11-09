namespace ZettaTrade.Infrastructure.SingleInstance;

using System;

/// <summary>
/// What the single-instance dispatch decided about one process start. The
/// decision is made before anything heavy boots: an owned start builds the
/// application normally, a redirected start never even touches Avalonia —
/// it forwards (or refuses) the argument vector and the process returns the
/// mapped exit code right away.
/// </summary>
public abstract record LaunchOutcome
{
    /// <summary>
    /// This start acquired the dispatch claim and becomes the running
    /// instance. The claim must be held for the whole process lifetime —
    /// releasing it early would let a concurrent start build a second app.
    /// </summary>
    /// <param name="InstanceClaim">The claim the process keeps until exit.</param>
    public sealed record Owned(IDisposable InstanceClaim) : LaunchOutcome
    {
    }

    /// <summary>
    /// A running instance exists, so this start resolves through the handoff
    /// lane and exits promptly with the outcome's own exit code. The detail
    /// rides for the dispatch file log only (never user-facing).
    /// </summary>
    /// <param name="ExitCode">The mapped shell outcome of the redirect.</param>
    /// <param name="Detail">The file-log word of the settled forward or refusal.</param>
    public sealed record Redirected(LaunchExitCode ExitCode, string Detail) : LaunchOutcome
    {
    }
}
