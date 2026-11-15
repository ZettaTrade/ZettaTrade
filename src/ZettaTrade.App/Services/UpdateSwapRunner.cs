namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// The staged update's startup runner (T-258): the manifest state machine
/// that decides what the NEXT start does about a pending update. It runs on
/// the owned lane of <c>Program.Main</c> — before any application code —
/// and never touches Avalonia, the settings store or the network.
/// <para>
/// The states: <c>Approved</c> means the user consented to an update that
/// replaces the installed build — the runner swaps (the current build's
/// files move into <c>.update/rollback</c>, the staged build moves into the
/// install directory), writes <c>Applied</c>, and relaunches the new build,
/// whose exit is this process's prompt end. <c>Applied</c> means a previous
/// start applied the swap and never proved healthy — the runner rolls back
/// (the failed build's files move into <c>.update/discard</c>, the rollback
/// copy returns), so the previous build serves again. The healthy point is
/// the composition walk's completion (<see cref="MarkHealthy"/>): reaching
/// it writes <c>Settled</c> and cleans the staging tree. A crash before the
/// walk leaves <c>Applied</c> standing — the next start reads exactly that.
/// </para>
/// <para>
/// The mechanics honor Windows' file locks: a running image cannot be
/// deleted or overwritten, but it CAN be renamed — every swap step moves
/// files (never deletes them) and a step that cannot move restores the
/// whole swap. A relaunch failure restores too: the consented update never
/// leaves the app unable to start. Everything the runner does lands in
/// <see cref="UpdateSwapLog"/> — the only stdout the process ever writes is
/// MCP protocol traffic, and this lane is never that.
/// </para>
/// </summary>
public static class UpdateSwapRunner
{
    #region Public API

    /// <summary>What the startup should do after the runner returned.</summary>
    public enum StartupOutcome
    {
        /// <summary>No pending work (or the swap already applied): continue starting normally.</summary>
        Continue = 0,

        /// <summary>The swap applied and the new build was spawned: this process must exit promptly.</summary>
        Relaunched = 1,
    }

    /// <summary>
    /// Run the pending-swap decision for THIS start. Called once, on the
    /// owned lane, before the composition walk. The relaunch path requires
    /// the caller to release the single-instance claim BEFORE returning
    /// (the spawned build claims it for its own owned start).
    /// </summary>
    /// <param name="installDirectory">The app's install directory.</param>
    /// <param name="launchArguments">The original launch arguments the relaunch carries forward.</param>
    /// <param name="processPath">The running exe's path (the app host the relaunch spawns); the real <c>Environment.ProcessPath</c> when omitted.</param>
    /// <param name="spawn">The relaunch seam (the test constructor pins its own receipt); the real process spawn when omitted.</param>
    /// <returns>Whether the runner applied a swap and spawned the new build.</returns>
    public static StartupOutcome RunOnStartup(
        string installDirectory,
        IReadOnlyList<string> launchArguments,
        string? processPath = null,
        Func<string, IReadOnlyList<string>, bool>? spawn = null)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("The install directory must be a real path.", nameof(installDirectory));
        }

        var hostFileName = Path.GetFileName(processPath ?? Environment.ProcessPath ?? string.Empty);
        if (hostFileName.Length == 0)
        {
            UpdateSwapLog.Warning("The swap runner cannot resolve the app host's file name; the pending state stands");
            return StartupOutcome.Continue;
        }

        var store = new UpdateStagingStore(installDirectory);
        var manifest = store.TryRead();
        switch (manifest)
        {
            case null:
                // No pending update (or an unparsable manifest): the sweep
                // keeps the staging tree bounded — a crashed staging's
                // orphans die here, referenced state never does.
                store.SweepOrphans();
                return StartupOutcome.Continue;

            case { State: UpdateSwapState.Staged }:
                // The consent question is pending: a staged update never
                // applies by itself. The staging service asks; the settings
                // off switch silences the whole loop.
                UpdateSwapLog.Information(
                    "The staged update {Version} waits for consent; nothing applies",
                    manifest.Version);
                return StartupOutcome.Continue;

            case { State: UpdateSwapState.Approved }:
                return Swap(store, manifest, hostFileName, launchArguments, spawn ?? TrySpawn)
                    ? StartupOutcome.Relaunched
                    : StartupOutcome.Continue;

            case { State: UpdateSwapState.Applied }:
                // A previous start applied the swap and this is a later
                // start: the proof never arrived, so the new build failed
                // to launch. Restore the previous build.
                Rollback(store, manifest, hostFileName);
                return StartupOutcome.Continue;

            case { State: UpdateSwapState.Settled }:
                // The new build launched and proved healthy (its walk
                // completed): the update is done, the parking spots die.
                UpdateSwapLog.Information(
                    "The update {Version} settled: the previous build's copy and the staging tree clean up",
                    manifest.Version);
                store.CleanupSettled();
                return StartupOutcome.Continue;

            default:
                return StartupOutcome.Continue;
        }
    }

    /// <summary>
    /// Mark the RUNNING build healthy: the composition walk completed, so
    /// this start is the applied update's successful launch. Non-throwing
    /// by design — a fault here is a swap-log warning, never a startup
    /// failure; the state stays <c>Applied</c> and the next start rolls
    /// back (the honest worst case: a healthy build that failed to mark
    /// itself serves the previous build again).
    /// </summary>
    internal static void MarkHealthy(string installDirectory)
    {
        try
        {
            var store = new UpdateStagingStore(installDirectory);
            var manifest = store.TryRead();
            if (manifest is not { State: UpdateSwapState.Applied })
            {
                return;
            }

            store.Write(manifest with { State = UpdateSwapState.Settled });
            store.CleanupSettled();
            UpdateSwapLog.Information(
                "The applied update {Version} proved healthy: the swap settles",
                manifest.Version);
        }
        catch (Exception ex)
        {
            UpdateSwapLog.Warning(ex, "The healthy launch could not settle the applied update: the next start reads the honest Applied state");
        }
    }

    #endregion Public API

    #region The swap

    /// <summary>
    /// Apply the consented swap: the current build's files move into the
    /// rollback parking spot, the staged build's files move into the
    /// install directory, the manifest writes <c>Applied</c>, and the new
    /// build spawns. Any step that cannot complete restores the pre-swap
    /// layout — a consented update never leaves the install directory in a
    /// state the previous build cannot start from.
    /// </summary>
    /// <param name="spawn">The relaunch lane (the test receipt or the real spawn).</param>
    private static bool Swap(
        UpdateStagingStore store,
        UpdatePendingManifest manifest,
        string hostFileName,
        IReadOnlyList<string> launchArguments,
        Func<string, IReadOnlyList<string>, bool> spawn)
    {
        var stagedDirectory = store.StagedDirectory(manifest.Version);
        var stagedHostPath = Path.Combine(stagedDirectory, hostFileName);
        if (!File.Exists(stagedHostPath))
        {
            // The staged build lost its app host (a corrupted stage): the
            // consented update cannot apply — the staging tree dies and the
            // next check re-stages from a verified download.
            UpdateSwapLog.Warning(
                "The staged update {Version} lost its app host ({Host}); the stage discards and the update re-offers",
                manifest.Version,
                hostFileName);
            store.CleanupSettled();
            return false;
        }

        if (!ClearDirectory(store.RollbackDirectory))
        {
            UpdateSwapLog.Warning(
                "The rollback parking spot could not clear for the {Version} swap; the swap retries on the next start",
                manifest.Version);
            return false;
        }

        var movedOut = new List<(string Source, string Target)>();
        if (!MoveDirectoryContents(store.InstallDirectory, store.RollbackDirectory, SkipStagingTree, movedOut))
        {
            UpdateSwapLog.Warning(
                "The {Version} swap could not move the current build aside; the swap restores and retries on the next start",
                manifest.Version);
            Restore(movedOut);
            TryDeleteDirectoryIfEmpty(store.RollbackDirectory);
            return false;
        }

        var movedIn = new List<(string Source, string Target)>();
        if (!MoveDirectoryContents(stagedDirectory, store.InstallDirectory, static _ => false, movedIn))
        {
            UpdateSwapLog.Warning(
                "The staged {Version} build could not move into place; the swap restores the previous build",
                manifest.Version);
            Restore(movedIn);
            Restore(movedOut);
            store.Write(manifest);
            TryDeleteDirectoryIfEmpty(store.RollbackDirectory);
            return false;
        }

        store.Write(manifest with { State = UpdateSwapState.Applied, ProofRequired = false });
        UpdateSwapLog.Information(
            "The update {Version} applied: {Files} file(s) swapped, the previous build parked for rollback",
            manifest.Version,
            movedIn.Count);

        // The relaunch rides the SWAPPED-IN host: the staged tree's files
        // moved into the install directory, so the staged path no longer
        // exists — the new build starts from its installed home.
        if (spawn(Path.Combine(store.InstallDirectory, hostFileName), launchArguments))
        {
            return true;
        }

        // The relaunch failed: continuing would run the previous build's
        // images over the new build's files on disk (lazy assembly loads
        // would mix builds). Restore the pre-swap layout instead — the
        // consented update stays consented and retries on the next start.
        UpdateSwapLog.Warning(
            "The {Version} relaunch failed to spawn; the swap restores the previous build and the consent stands",
            manifest.Version);
        var restoredIn = new List<(string Source, string Target)>();
        MoveDirectoryContents(store.InstallDirectory, stagedDirectory, SkipStagingTree, restoredIn);
        Restore(movedOut);
        store.Write(manifest);
        TryDeleteDirectoryIfEmpty(store.RollbackDirectory);
        return false;
    }

    #endregion The swap

    #region The rollback

    /// <summary>
    /// Roll the failed applied build back: the current (failed) build's
    /// files move into the discard spot, the rollback copy returns, the
    /// staged tree and the manifest die. The current process keeps running
    /// (its images are mapped) — the restored build serves from the next
    /// start on, which is the acceptance's promise: a failed launch never
    /// strands the installation.
    /// </summary>
    private static void Rollback(UpdateStagingStore store, UpdatePendingManifest manifest, string hostFileName)
    {
        var rollbackHost = Path.Combine(store.RollbackDirectory, hostFileName);
        if (!File.Exists(rollbackHost))
        {
            // The previous build's copy is gone: there is nothing to
            // restore. Keep what is on disk (the applied build) and let the
            // staging tree die — the honest end of a lost rollback copy.
            UpdateSwapLog.Warning(
                "The rollback copy of {Host} is missing: the applied {Version} build stays and the rollback copy dies",
                hostFileName,
                manifest.Version);
            store.CleanupSettled();
            return;
        }

        var discardDirectory = store.NewDiscardDirectory();
        var movedAside = new List<(string Source, string Target)>();
        if (!MoveDirectoryContents(store.InstallDirectory, discardDirectory, SkipStagingTree, movedAside))
        {
            UpdateSwapLog.Warning(
                "The failed {Version} build could not move aside for the rollback; the state stands and the next start retries",
                manifest.Version);
            Restore(movedAside);
            return;
        }

        var restoredBack = new List<(string Source, string Target)>();
        if (!MoveDirectoryContents(store.RollbackDirectory, store.InstallDirectory, static _ => false, restoredBack))
        {
            UpdateSwapLog.Warning(
                "The previous build could not return for the {Version} rollback; the state stands and the next start retries",
                manifest.Version);
            Restore(restoredBack);
            Restore(movedAside);
            return;
        }

        // The whole staging subtree dies (the manifest, the rollback spot,
        // the discarded failed build — the discard spot lives inside it):
        // the restored previous build is the only authority on disk.
        store.CleanupSettled();
        UpdateSwapLog.Warning(
            "The {Version} build failed its launch: rolled back to the previous build ({Files} file(s) restored)",
            manifest.Version,
            restoredBack.Count);
    }

    #endregion The rollback

    #region The move machinery

    /// <summary>The move-out predicate: the staging subtree never leaves the install directory.</summary>
    private static bool SkipStagingTree(string name) =>
        string.Equals(name, UpdateStagingStore.StagingDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Move a directory's whole content into another directory, entry by
    /// entry. The first failure stops the pass and answers false with the
    /// completed moves in <paramref name="journal"/> — the caller restores.
    /// Locked running images rename (Windows' move-while-running rule); a
    /// genuinely immovable file is the failure the restore covers.
    /// </summary>
    private static bool MoveDirectoryContents(
        string sourceDirectory,
        string targetDirectory,
        Func<string, bool> skip,
        List<(string Source, string Target)> journal)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var source in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (skip(name))
            {
                continue;
            }

            var target = Path.Combine(targetDirectory, name);
            try
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    // A same-named target means the source move-out left a
                    // leftover: refuse rather than overwrite — the restore
                    // keeps every file the pass promised to keep.
                    throw new IOException($"The target {name} already exists; the swap never overwrites silently.");
                }

                if (Directory.Exists(source))
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target);
                }

                journal.Add((source, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UpdateSwapLog.Warning(ex, "The move of {Name} failed: the pass stops and the caller restores", name);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ride a journald pass back: every completed move returns to its
    /// source place, last move first — the file that left its home LAST is
    /// the first to return. The entry recorded source→target; the restore
    /// moves target→source.
    /// </summary>
    private static void Restore(List<(string Source, string Target)> journal)
    {
        for (var index = journal.Count - 1; index >= 0; index--)
        {
            var (source, target) = journal[index];
            try
            {
                if (File.Exists(target))
                {
                    File.Move(target, source);
                }
                else if (Directory.Exists(target))
                {
                    Directory.Move(target, source);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UpdateSwapLog.Warning(ex, "The restore of {Source} failed: the layout needs the next start's pass", source);
            }
        }

        journal.Clear();
    }

    /// <summary>Clear one directory tree (the rollback spot renews for every swap).</summary>
    private static bool ClearDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateSwapLog.Warning(ex, "The clearing of {Path} failed", path);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Delete a parking directory ONLY when its content is gone — the
    /// restore's empty shell dies here, a shell that still holds files is
    /// the honest leftover the next pass reads.
    /// </summary>
    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateSwapLog.Warning(ex, "The empty parking spot {Path} could not delete now: a later pass sweeps it", path);
        }
    }

    #endregion The move machinery

    #region The relaunch

    /// <summary>
    /// The real relaunch: spawn the new build's app host with the original
    /// launch arguments (the shell never mediates — the arguments ride the
    /// process's own list, unquoted and unsplit).
    /// </summary>
    private static bool TrySpawn(string hostPath, IReadOnlyList<string> launchArguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
            };
            foreach (var argument in launchArguments)
            {
                start.ArgumentList.Add(argument);
            }

            return Process.Start(start) is not null;
        }
        catch (Exception ex)
        {
            UpdateSwapLog.Warning(ex, "The relaunch of {Host} failed", hostPath);
            return false;
        }
    }

    #endregion The relaunch
}
