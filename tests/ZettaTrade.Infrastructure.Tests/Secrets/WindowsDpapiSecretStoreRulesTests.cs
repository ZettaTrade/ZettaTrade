namespace ZettaTrade.Infrastructure.Tests.Secrets;

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Secrets;

/// <summary>
/// T-049 store-rule tests for the Windows DPAPI secret store:
/// <list type="bullet">
/// <item>BUG-010: a failed disk write or removal-save surfaces instead of a
/// claimed success that the restart undoes; the store stays healthy once the
/// file is writable again.</item>
/// <item>BUG-011: <see cref="ZettaTrade.Core.Interfaces.ISecretStore.ListKeysAsync"/>
/// awaits the shared lock (no synchronous wait inside an async contract
/// method).</item>
/// <item>BUG-039: initialization runs under the same lock as the reads and
/// writes, so an early concurrent <c>SetAsync</c> is never clobbered by the
/// load's cache reassignment and its secret survives the next save.</item>
/// <item>BUG-040: a decryption failure is distinguishable from a missing key
/// (it surfaces; null stays reserved for absent keys).</item>
/// </list>
/// Encryption-dependent facts guard on the runtime; the listing fact runs
/// everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStoreRulesTests
{
    /// <summary>The in-memory hold time that stretches the lock-contention window.</summary>
    private static readonly TimeSpan ContentionHold = TimeSpan.FromSeconds(2);

    private static string NewStoreDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t049-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// BUG-010: with the store file held exclusively, both the write and the
    /// removal must throw (the old code swallowed the failure and reported
    /// success), and once the file is writable again the store keeps working.
    /// </summary>
    [Fact]
    public async Task SetAndRemoveAsync_WhenTheStoreFileCannotBeWritten_SurfaceInsteadOfClaimingSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The DPAPI encryption below is Windows-only.
            return;
        }

        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            var store = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await store.InitializeAsync();
            await store.SetAsync("survivor", "survivor-value");

            await using (var hold = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                var set = () => store.SetAsync("locked-key", "locked-value");
                var thrownSet = await set.Should().ThrowAsync<Exception>("a failed disk write must surface, not claim success");
                thrownSet.And.Should().BeAssignableTo<UnauthorizedAccessException>(
                    "Windows refuses the rename over an exclusively held target");

                var remove = () => store.RemoveAsync("survivor");
                var thrownRemove = await remove.Should().ThrowAsync<Exception>("a failed removal save must surface too");
                thrownRemove.And.Should().BeAssignableTo<UnauthorizedAccessException>();
            }

            // Releasing the file restores a fully healthy store.
            await store.SetAsync("locked-key", "locked-value");
            var reloaded = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await reloaded.InitializeAsync();
            (await reloaded.GetAsync("locked-key")).Should().Be("locked-value");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BUG-011: a listing under lock contention stays genuinely awaitable — it
    /// remains pending for as long as the holder keeps the lock, then completes
    /// through the (awaited) acquisition instead of any synchronous wait.
    /// </summary>
    [Fact]
    public async Task ListKeysAsync_StaysGenuinelyPendingWhileTheSharedLockIsHeld()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            var store = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await store.InitializeAsync();

            // Rent the store's shared lock through reflection: the listing
            // must be genuinely pending while the lock is held away from it.
            var lockField = typeof(WindowsDpapiSecretStore).GetField(
                "_lock",
                BindingFlags.NonPublic | BindingFlags.Instance);
            lockField.Should().NotBeNull("the shared lock seam the listing awaits");
            var sharedLock = (SemaphoreSlim)lockField!.GetValue(store)!;
            await sharedLock.WaitAsync();
            var released = false;
            try
            {
                var listingTask = store.ListKeysAsync();
                listingTask.IsCompleted.Should().BeFalse(
                    "a held lock keeps the listing genuinely pending (no synchronous short-circuit)");

                await Task.Delay(ContentionHold);
                listingTask.IsCompleted.Should().BeFalse(
                    "the listing still waits instead of stealing the busy lock");

                sharedLock.Release();
                released = true;

                var keys = await listingTask;
                keys.Should().BeEmpty();
            }
            finally
            {
                if (!released)
                {
                    sharedLock.Release();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BUG-039: an early <c>SetAsync</c> racing the (formerly unlocked) load
    /// is not clobbered by the load's cache reassignment — and the following
    /// save persists the FULL state, not the loaded snapshot that lost the
    /// secret.
    /// </summary>
    [Fact]
    public async Task AnEarly_SetAsync_RacingInitialization_SurvivesTheNextSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The DPAPI encryption below is Windows-only.
            return;
        }

        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");

            var seeding = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await seeding.InitializeAsync();
            await seeding.SetAsync("old-key", "old-value");

            // The race: a SetAsync queued while InitializeAsync's load is in
            // flight; the post-fix load runs under the same lock, so the set
            // applies ON TOP of the loaded state.
            var store = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            var initialization = store.InitializeAsync();
            var earlySet = store.SetAsync("new-key", "new-value");
            await Task.WhenAll(initialization, earlySet);

            // The divider save persists whatever the memory cache truly holds:
            // a pre-fix load-after-set would here erase the set secret from disk.
            await store.SetAsync("late-key", "late-value");

            var reloaded = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await reloaded.InitializeAsync();
            (await reloaded.GetAsync("old-key")).Should().Be("old-value");
            (await reloaded.GetAsync("new-key")).Should().Be("new-value", "the early set survives the load and the next save");
            (await reloaded.GetAsync("late-key")).Should().Be("late-value");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BUG-040: a corrupt STORED value is a decryption failure, never a
    /// missing key: <c>GetAsync</c> throws (with an explicit message), while
    /// the key itself keeps appearing in the listing — the two outcomes are
    /// distinguishable.
    /// </summary>
    [Fact]
    public async Task ACorruptStoredValue_ThrowsInsteadOfReportingTheKeyMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The DPAPI encryption below is Windows-only.
            return;
        }

        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            // Valid store JSON whose value is base64 garbage: the loader
            // accepts it (it cannot verify encryptions ahead of use), so the
            // corruption is discovered by the decrypt attempt only.
            await File.WriteAllTextAsync(
                path,
                "{\"corrupt-key\": \"" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) + "\"}");

            var store = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            var init = async () => await store.InitializeAsync();
            await init.Should().NotThrowAsync("the file parses fine; the corruption hides inside the value");

            var keys = await store.ListKeysAsync();
            keys.Should().Contain("corrupt-key", "the KEY is present; only its content is corrupt");

            var get = () => store.GetAsync("corrupt-key");
            var thrown = await get.Should().ThrowAsync<InvalidOperationException>();
            thrown.And.Message.Should().Contain("NOT a missing key");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
