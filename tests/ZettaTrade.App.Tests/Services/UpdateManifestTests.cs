namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The staging store's battery (T-258): the manifest is the ONLY authority
/// the swap runner acts on, so its writes are atomic (a crash mid-write
/// never leaves a half manifest), its reads are tolerant (an unparsable
/// file is the honest none for decisions), and its layout keeps EVERYTHING
/// inside one <c>.update</c> subtree of the install directory — the user's
/// data lives in the profile directories, so a swap can never touch it.
/// </summary>
public sealed class UpdateManifestTests
{
    #region Tests: the manifest round trip

    /// <summary>A written manifest reads back with every fact intact.</summary>
    [Fact]
    public void AWrittenManifest_RoundTripsItsFacts()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            var stagedAt = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
            var manifest = new UpdatePendingManifest(
                "0.2.2",
                UpdateSwapState.Approved,
                ProofRequired: false,
                "zettatrade-0.2.2-win-x64.zip",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                stagedAt);

            store.Write(manifest);

            var read = store.TryRead();
            read.Should().NotBeNull();
            read!.Version.Should().Be("0.2.2");
            read.State.Should().Be(UpdateSwapState.Approved);
            read.ProofRequired.Should().BeFalse();
            read.AssetName.Should().Be("zettatrade-0.2.2-win-x64.zip");
            read.AssetSha256.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            read.StagedUtc.Should().Be(stagedAt);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>The state serializes by NAME (a hand-edited file stays readable) and an unknown state reads as none.</summary>
    [Fact]
    public void TheState_RidesItsName_AndAnUnknownOneReadsAsNone()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            Directory.CreateDirectory(store.StagingDirectory);
            File.WriteAllText(
                store.ManifestPath,
                """{"version":"0.2.2","state":"approved","proofRequired":false,"assetName":"a.zip","assetSha256":"00","stagedUtc":"2026-11-15T12:00:00Z"}""");

            store.TryRead().Should().NotBeNull().And.Subject.As<UpdatePendingManifest>().State
                .Should().Be(UpdateSwapState.Approved, "the state rides its name — the file stays hand-editable");

            File.WriteAllText(store.ManifestPath, """{"version":"0.2.2","state":"flying","proofRequired":false}""");
            store.TryRead().Should().BeNull("an unknown state is the honest none for decisions — the file stays for the runner");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>A missing, corrupt or empty manifest file reads as the honest none.</summary>
    [Fact]
    public void AMissingOrCorruptManifest_ReadsAsNone()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            store.TryRead().Should().BeNull("no file is no pending update");

            Directory.CreateDirectory(store.StagingDirectory);
            File.WriteAllText(store.ManifestPath, "{ not json at all");
            store.TryRead().Should().BeNull("a corrupt manifest never drives a swap");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>An atomic write leaves no temp file behind — a crashed write never lingers into the next start.</summary>
    [Fact]
    public void AWrite_LeavesNoTempFileBehind()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            store.Write(new UpdatePendingManifest(
                "0.2.2",
                UpdateSwapState.Staged,
                ProofRequired: false,
                "a.zip",
                "00",
                DateTimeOffset.UtcNow));

            File.Exists(store.ManifestPath).Should().BeTrue("the manifest itself stands");
            File.Exists(store.ManifestPath + ".tmp").Should().BeFalse("the temp move completed — nothing lingers");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    #endregion

    #region Tests: the layout

    /// <summary>Everything the store touches lives inside one <c>.update</c> subtree of the install directory.</summary>
    [Fact]
    public void TheLayout_KeepsEverythingInsideTheStagingSubtree()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            var installRoot = Path.GetFullPath(directory);

            store.InstallDirectory.Should().Be(installRoot);
            store.StagingDirectory.Should().Be(Path.Combine(installRoot, ".update"));
            store.ManifestPath.Should().Be(Path.Combine(installRoot, ".update", "manifest.json"));
            store.RollbackDirectory.Should().Be(Path.Combine(installRoot, ".update", "rollback"));
            store.StagedRootDirectory.Should().Be(Path.Combine(installRoot, ".update", "staged"));
            store.StagedDirectory("0.2.2").Should().Be(Path.Combine(installRoot, ".update", "staged", "0.2.2"));
            store.DiscardRootDirectory.Should().Be(Path.Combine(installRoot, ".update", "discard"));

            var discard = store.NewDiscardDirectory();
            discard.StartsWith(store.DiscardRootDirectory, StringComparison.Ordinal).Should().BeTrue(
                "a discard spot lives under the discard root, timestamped per attempt");
            discard.Should().NotBe(store.NewDiscardDirectory(), "two attempts never share a discard spot");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>A write outside a real install directory is the honest refusal.</summary>
    [Fact]
    public void AnEmptyInstallDirectory_IsTheHonestRefusal()
    {
        var act = () => new UpdateStagingStore("   ");
        act.Should().Throw<ArgumentException>("an install directory is a real path, never a guess");
    }

    #endregion

    #region Tests: the hygiene

    /// <summary>The orphan sweep runs only when NO manifest exists — a referenced staged tree never dies.</summary>
    [Fact]
    public void TheOrphanSweep_CleansOnlyManifestlessTrees()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);

            Directory.CreateDirectory(store.StagedDirectory("0.1.9"));
            File.WriteAllText(Path.Combine(store.StagedDirectory("0.1.9"), "orphan.txt"), "leftover");
            Directory.CreateDirectory(store.DiscardRootDirectory);
            File.WriteAllText(store.ManifestPath + ".tmp", "half a write");

            store.SweepOrphans();

            Directory.Exists(store.StagedRootDirectory).Should().BeFalse("an orphaned staged tree dies");
            Directory.Exists(store.DiscardRootDirectory).Should().BeFalse("an orphaned discard tree dies");
            File.Exists(store.ManifestPath + ".tmp").Should().BeFalse("a crashed write's temp dies");

            // With a manifest standing, the referenced tree stays.
            store.Write(new UpdatePendingManifest(
                "0.2.2",
                UpdateSwapState.Staged,
                ProofRequired: false,
                "a.zip",
                "00",
                DateTimeOffset.UtcNow));
            Directory.CreateDirectory(store.StagedDirectory("0.2.2"));
            File.WriteAllText(Path.Combine(store.StagedDirectory("0.2.2"), "host.txt"), "staged build");

            store.SweepOrphans();

            Directory.Exists(store.StagedDirectory("0.2.2")).Should().BeTrue(
                "a referenced staged tree never sweeps");
            File.Exists(store.ManifestPath).Should().BeTrue("the manifest itself never sweeps");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>The settled cleanup removes the parking spots and the manifest — the update is done.</summary>
    [Fact]
    public void TheSettledCleanup_RemovesTheParkingSpots()
    {
        var directory = NewTempDirectory();
        try
        {
            var store = new UpdateStagingStore(directory);
            Directory.CreateDirectory(store.RollbackDirectory);
            Directory.CreateDirectory(store.StagedDirectory("0.2.2"));
            store.Write(new UpdatePendingManifest(
                "0.2.2",
                UpdateSwapState.Settled,
                ProofRequired: false,
                "a.zip",
                "00",
                DateTimeOffset.UtcNow));

            store.CleanupSettled();

            Directory.Exists(store.RollbackDirectory).Should().BeFalse();
            Directory.Exists(store.StagedRootDirectory).Should().BeFalse();
            File.Exists(store.ManifestPath).Should().BeFalse("a settled update has no pending state left");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    #endregion

    #region Helpers

    private static string NewTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "t258-manifest-" + Guid.NewGuid().ToString("N"))).FullName;

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
