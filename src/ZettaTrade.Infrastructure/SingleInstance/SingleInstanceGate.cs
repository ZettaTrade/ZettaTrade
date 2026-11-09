namespace ZettaTrade.Infrastructure.SingleInstance;

using System.Collections.Generic;

/// <summary>
/// The single-instance gate: the decision layer one process start rides
/// before anything boots. Try to own the dispatch claim; failing that, the
/// start is a redirect — its vector is walked through the allowlist
/// grammar first (a malformed vector is a rejection: nothing forwards,
/// nothing applies, the process exits promptly with its code), and a
/// well-shaped vector forwards to the running instance through the bounded
/// handoff lane. The layer stays a pure decision surface: every outcome
/// carries its own detail word for the dispatch file log, and nothing
/// here writes stdout.
/// </summary>
public static class SingleInstanceGate
{
    /// <summary>
    /// Decide the caller's launch. The claim attempt is the cheapest and
    /// highest-fidelity signal (the OS lock decides, no races): a start
    /// that owns it boots the app and keeps the claim for its whole
    /// process lifetime, exactly as the outcome says.
    /// </summary>
    /// <param name="args">The raw vector, exactly as the shell handed it over.</param>
    /// <returns>The owned launch (build the app), or the redirect (forward or refusal already settled; return its exit code).</returns>
    public static LaunchOutcome Enter(IReadOnlyList<string>? args) =>
        Enter(args, SingleInstanceNames.LockFilePath, SingleInstanceNames.PipeName);

    /// <summary>The decision with the dispatch identifiers injected (tests compose their own lanes).</summary>
    internal static LaunchOutcome Enter(
        IReadOnlyList<string>? args,
        string lockFilePath,
        string pipeName,
        HandoffForwardSchedule? schedule = null)
    {
        var claim = SingleInstanceClaim.TryAcquire(lockFilePath);
        if (claim is not null)
        {
            return new LaunchOutcome.Owned(claim);
        }

        var parsed = HandoffArgParser.Parse(args);
        if (parsed.Rejection is not null)
        {
            // The refusal is the whole outcome: no forward leaves this
            // process, nothing may ever apply, and the shell sees the
            // rejection code. No argument values ever ride the outcome —
            // the reason and the vector's shape only.
            return new LaunchOutcome.Redirected(
                LaunchExitCode.ArgsRejected,
                $"the grammar refused the vector ({parsed.Rejection}, {args?.Count ?? 0} token(s))");
        }

        var forwarded = HandoffForwarder.Forward(pipeName, args ?? [], schedule ?? HandoffForwardSchedule.Production);
        return new LaunchOutcome.Redirected(forwarded.ExitCode, forwarded.Detail);
    }
}
