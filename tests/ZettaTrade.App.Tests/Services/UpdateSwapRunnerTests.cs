namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The swap runner's goldens (T-258): the state machine acts ONLY on the
/// manifest and ONLY inside the install directory's <c>.update</c>
/// subtree — the previous build parks in <c>rollback</c>, the staged build
/// moves in, the manifest carries the state, and every step that cannot
/// complete restores the pre-swap layout (a consented update never leaves
/// the install unable to start). The relaunch rides a test seam: a unit
/// fact never spawns a real process.
/// </summary>
public sealed class UpdateSwapRunnerTests
{
    /// <summary>The host file name every fixture build carries (the app host the swap moves and spawns).</summary>
    private const string HostFileName = "zettatrade.exe";

    private const string Version = "0.2.2";

    #region Tests: the wait and the swap

    /// <summary>A staged manifest never applies by itself: the consent question is pending, the layout stands.</summary>
    [Fact]
    public void AStagedManifest_NeverAppliesByItself()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "old-build");
            SeedStagedBuild(store, Version, "new-build");
            store.Write(Manifest(Version, UpdateSwapState.Staged));

            var outcome = UpdateSwapRunner.RunOnStartup(install, ["--workspace", "main"]);

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue, "a staged update waits for its consent");
            File.ReadAllText(Path.Combine(install, "old-build")).Should().Be("old-build", "nothing moved");
            Directory.Exists(store.RollbackDirectory).Should().BeFalse("no parking spot appears before the swap");
            ReadManifest(store)!.State.Should().Be(UpdateSwapState.Staged);
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>The consented swap moves the staged build in, parks the previous build, writes Applied and spawns.</summary>
    [Fact]
    public void AnApprovedManifest_SwapsParksAndApplies()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "old-build");
            SeedStagedBuild(store, Version, "new-build");
            store.Write(Manifest(Version, UpdateSwapState.Approved));

            string? spawnedHost = null;
            IReadOnlyList<string>? spawnedArguments = null;
            var outcome = UpdateSwapRunner.RunOnStartup(
                install,
                ["--workspace", "main"],
                processPath: Path.Combine(install, HostFileName),
                spawn: (host, arguments) =>
                {
                    spawnedHost = host;
                    spawnedArguments = arguments;
                    return true;
                });

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Relaunched, "the swap applied and the new build spawned");
            File.ReadAllText(Path.Combine(install, "new-build")).Should().Be("new-build", "the staged build moved in");
            File.ReadAllText(Path.Combine(store.RollbackDirectory, "old-build")).Should().Be(
                "old-build",
                "the previous build parked for the rollback");
            File.Exists(Path.Combine(store.RollbackDirectory, HostFileName)).Should().BeTrue("the parked host is included");
            ReadManifest(store)!.State.Should().Be(UpdateSwapState.Applied, "the applied state owes the healthy proof");
            spawnedHost.Should().Be(Path.Combine(install, HostFileName), "the spawn rode the swapped-in host");
            spawnedArguments.Should().Equal(["--workspace", "main"], "the launch arguments carry forward");
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>A relaunch failure restores the pre-swap layout — the consent stands and the next start retries.</summary>
    [Fact]
    public void ASpawnFailure_RestoresThePreSwapLayout()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "old-build");
            SeedStagedBuild(store, Version, "new-build");
            store.Write(Manifest(Version, UpdateSwapState.Approved));

            var outcome = UpdateSwapRunner.RunOnStartup(
                install,
                [],
                processPath: Path.Combine(install, HostFileName),
                spawn: (host, arguments) => false);

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue, "a failed relaunch is a failed swap");
            File.ReadAllText(Path.Combine(install, "old-build")).Should().Be("old-build", "the previous build returned");
            File.ReadAllText(Path.Combine(install, HostFileName)).Should().Be("old-host", "the parked host returned too");
            File.ReadAllText(Path.Combine(store.StagedDirectory(Version), "new-build")).Should().Be(
                "new-build",
                "the swapped-in files returned to the staged tree");
            ReadManifest(store)!.State.Should().Be(UpdateSwapState.Approved, "the consent stands for the next start");
            Directory.Exists(store.RollbackDirectory).Should().BeFalse("the parking spot emptied back out");
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>A corrupted stage (the app host lost) refuses the swap and discards the staging tree.</summary>
    [Fact]
    public void AHostlessStage_RefusesTheSwap_AndDiscards()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "old-build");
            Directory.CreateDirectory(store.StagedDirectory(Version));
            File.WriteAllText(Path.Combine(store.StagedDirectory(Version), "data.bin"), "partial");
            store.Write(Manifest(Version, UpdateSwapState.Approved));

            var outcome = UpdateSwapRunner.RunOnStartup(
                install,
                [],
                processPath: Path.Combine(install, HostFileName),
                spawn: (host, arguments) => true);

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue, "a hostless stage never swaps");
            File.ReadAllText(Path.Combine(install, "old-build")).Should().Be("old-build", "the previous build never moved");
            ReadManifest(store).Should().BeNull("the unusable pending state died with the staging tree");
            Directory.Exists(store.StagedRootDirectory).Should().BeFalse("the discarded stage re-offers from a fresh download");
        }
        finally
        {
            TryDelete(install);
        }
    }

    #endregion

    #region Tests: the rollback and the settle

    /// <summary>A start seeing Applied (the proof never arrived) rolls the failed build back and restores the previous one.</summary>
    [Fact]
    public void AnAppliedManifest_RollsTheFailedBuildBack()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "failed-build"); // the applied (failed) build's files
            Directory.CreateDirectory(store.RollbackDirectory);
            File.WriteAllText(Path.Combine(store.RollbackDirectory, "old-build"), "old-build");
            File.WriteAllText(Path.Combine(store.RollbackDirectory, HostFileName), "old-host");
            store.Write(Manifest(Version, UpdateSwapState.Applied));

            var outcome = UpdateSwapRunner.RunOnStartup(
                install,
                [],
                processPath: Path.Combine(install, HostFileName));

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue, "the rollback serves the previous build this start");
            File.ReadAllText(Path.Combine(install, "old-build")).Should().Be("old-build", "the previous build returned");
            ReadManifest(store).Should().BeNull("the update is over — no pending state remains");
            Directory.Exists(store.StagingDirectory).Should().BeFalse(
                "the settled cleanup removed the parking spots and the staging tree");
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>The healthy launch proves itself: MarkHealthy settles an applied update and cleans up.</summary>
    [Fact]
    public void MarkHealthy_SettlesAnAppliedUpdate()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            store.Write(Manifest(Version, UpdateSwapState.Applied));
            Directory.CreateDirectory(store.RollbackDirectory);
            Directory.CreateDirectory(store.StagedDirectory(Version));

            UpdateSwapRunner.MarkHealthy(install);

            ReadManifest(store).Should().BeNull("the settled state cleaned itself up");
            Directory.Exists(store.RollbackDirectory).Should().BeFalse();
            Directory.Exists(store.StagedRootDirectory).Should().BeFalse();
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>MarkHealthy is a no-op without an applied manifest — a staged update keeps waiting.</summary>
    [Fact]
    public void MarkHealthy_LeavesEveryOtherStateStanding()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            store.Write(Manifest(Version, UpdateSwapState.Staged));

            UpdateSwapRunner.MarkHealthy(install);

            ReadManifest(store)!.State.Should().Be(UpdateSwapState.Staged, "a staged update is not a proven launch");
        }
        finally
        {
            TryDelete(install);
        }
    }

    #endregion

    #region Tests: the sweep

    /// <summary>A manifestless start sweeps the staging tree's orphans — a crashed staging's remains die.</summary>
    [Fact]
    public void AManifestlessStart_SweepsTheOrphans()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            Directory.CreateDirectory(store.StagedDirectory("0.1.9"));
            File.WriteAllText(Path.Combine(store.StagedDirectory("0.1.9"), "orphan.txt"), "leftover");
            File.WriteAllText(store.ManifestPath + ".tmp", "half a write");

            var outcome = UpdateSwapRunner.RunOnStartup(install, []);

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue);
            Directory.Exists(store.StagedRootDirectory).Should().BeFalse("the orphaned staged tree died");
            File.Exists(store.ManifestPath + ".tmp").Should().BeFalse("the crashed write's temp died");
        }
        finally
        {
            TryDelete(install);
        }
    }

    /// <summary>A settled manifest's start cleans the parking spots — the update is done and the tree dies.</summary>
    [Fact]
    public void ASettledManifest_CleansUpAndContinues()
    {
        var install = NewInstall();
        try
        {
            var store = new UpdateStagingStore(install);
            SeedBuild(install, "current-build");
            Directory.CreateDirectory(store.RollbackDirectory);
            Directory.CreateDirectory(store.StagedDirectory(Version));
            store.Write(Manifest(Version, UpdateSwapState.Settled));

            var outcome = UpdateSwapRunner.RunOnStartup(
                install,
                [],
                processPath: Path.Combine(install, HostFileName));

            outcome.Should().Be(UpdateSwapRunner.StartupOutcome.Continue, "the settled update changed nothing");
            File.ReadAllText(Path.Combine(install, "old-build")).Should().Be("current-build", "the running build stands");
            Directory.Exists(store.StagingDirectory).Should().BeFalse("the settled parking spots died");
        }
        finally
        {
            TryDelete(install);
        }
    }

    #endregion

    #region Fixtures

    private static string NewInstall() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "t258-swap-" + Guid.NewGuid().ToString("N"))).FullName;

    private static UpdatePendingManifest Manifest(string version, UpdateSwapState state) => new(
        version,
        state,
        ProofRequired: state == UpdateSwapState.Applied,
        "zettatrade-" + version + "-win-x64.zip",
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        DateTimeOffset.UtcNow);

    /// <summary>Seed the install directory with one previous build's files (the host included).</summary>
    private static void SeedBuild(string install, string marker)
    {
        File.WriteAllText(Path.Combine(install, HostFileName), "old-host");
        File.WriteAllText(Path.Combine(install, "old-build"), marker);
    }

    /// <summary>Seed one version's staged build (the host included).</summary>
    private static void SeedStagedBuild(UpdateStagingStore store, string version, string marker)
    {
        var staged = store.StagedDirectory(version);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, HostFileName), "new-host");
        File.WriteAllText(Path.Combine(staged, "new-build"), marker);
    }

    private static UpdatePendingManifest? ReadManifest(UpdateStagingStore store) => store.TryRead();

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp space is the OS's to reclaim; a locked tree dies later.
        }
    }

    #endregion
}
