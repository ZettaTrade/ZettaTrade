namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.IO;

/// <summary>
/// The single-instance claim: an OS-enforced exclusive open on one
/// well-known file, held by a live process until it exits. The claim layer
/// is the one that is crash-safe and portable in the framework's own
/// semantics — <see cref="FileShare.None"/> is honored across processes by
/// the OS on Windows, macOS and Linux alike (a second exclusive open on a
/// claimed lock file fails with an I/O exception), and a process death
/// releases the claim by the OS itself, so a killed instance never wedges
/// the next launch. No named-mutex implementation quirks per OS get
/// involved, and the lock file is never deleted: the claim lives in the
/// file's exclusive open, not in its existence, and deleting after release
/// would re-introduce a claim race.
/// Thread model: acquired and released on the caller's thread during boot.
/// </summary>
internal sealed class SingleInstanceClaim : IDisposable
{
    private readonly FileStream _holder;

    /// <summary>
    /// Initialize a claim that is already successfully exclusive: only the
    /// factory's success branch constructs this.
    /// </summary>
    private SingleInstanceClaim(FileStream holder) => _holder = holder;

    /// <summary>
    /// Try to acquire the claim on the given lock file: one exclusive open,
    /// one decision. The lock file's directory is the claim layer's own
    /// artifact and is created first: a fresh profile — a first-ever launch,
    /// or the T-141 soak sandbox (every sandbox is fresh) — carries no data
    /// directory yet, and the pre-fix open threw
    /// <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/>),
    /// which the catch below read as "a live instance owns the claim" —
    /// every fresh-profile start redirect-exited with HandoffUnavailable.
    /// Whatever layer the OS rejects a double claim at, the caller receives
    /// the same null and leaves without touching anything.
    /// </summary>
    /// <param name="lockFilePath">The well-known lock file path.</param>
    /// <returns>The held claim, or null when a live instance owns it.</returns>
    public static SingleInstanceClaim? TryAcquire(string lockFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(lockFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var holder = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new SingleInstanceClaim(holder);
        }
        catch (IOException)
        {
            // The OS refused the exclusive open: a live instance holds the
            // claim. Nothing on disk is touched by this failed attempt.
            return null;
        }
    }

    /// <summary>
    /// Release the claim (the boot lane's orderly shutdown path; a process
    /// death releases it automatically by OS handle semantics). Releasing
    /// must have the effect that a subsequent claim on the same file
    /// succeeds immediately.
    /// </summary>
    public void Dispose() => _holder.Dispose();
}
