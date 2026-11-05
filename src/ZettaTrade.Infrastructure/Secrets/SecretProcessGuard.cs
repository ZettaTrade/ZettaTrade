namespace ZettaTrade.Infrastructure.Secrets;

using System.Diagnostics;

/// <summary>
/// The child-process death guard shared by the CLI-based platform secret
/// stores (macOS <c>security</c>, Linux <c>secret-tool</c>): a child whose
/// invocation timed out or was cancelled must be killed, because "using
/// Process" disposes only the handle — a surviving orphan can hold an open
/// keychain prompt or lock for an unbounded time (BUG-037).
/// </summary>
internal static class SecretProcessGuard
{
    /// <summary>
    /// Kill a child process that may still be running after its invocation
    /// failed; a process that already (or never) ran is skipped, and a race
    /// between the exit check and the kill is tolerated — this is best-effort
    /// cleanup on a path that rethrows its original failure afterwards.
    /// </summary>
    internal static void KillUnfinished(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The child exited (or was never started) while the kill was
            // attempted: nothing is left to kill and the original failure
            // path continues to throw, so nothing is swallowed here.
        }
    }
}
