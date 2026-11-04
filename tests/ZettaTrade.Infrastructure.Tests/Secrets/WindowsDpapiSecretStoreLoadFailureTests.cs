namespace ZettaTrade.Infrastructure.Tests.Secrets;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Secrets;

/// <summary>
/// T-046 / BUG-001: a DPAPI secret store whose file exists but cannot be read or
/// parsed (corrupt JSON, truncated JSON, invalid base64 values, an IO-locked
/// file) must fail loudly at initialization and then refuse every operation:
/// the file on disk stays byte-identical (secrets preserved, unusable-not-lost)
/// and the store never overwrites it. The happy path (a missing file bootstraps
/// an empty store that round-trips set/get/across-instances/remove) must stay
/// unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStoreLoadFailureTests
{
    private static string NewStoreDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t046-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Asserts that a store bound to the given store file fails initialization
    /// loudly and then refuses get/set/remove/list-key operations; reads no
    /// bytes itself, so a caller holding an exclusive file lock can use it too.
    /// </summary>
    private static async Task AssertRefusalBehaviorAsync(string path)
    {
        var store = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);

        var init = async () => await store.InitializeAsync();
        var initResult = await init.Should().ThrowAsync<InvalidOperationException>();
        initResult.And.Message.Should().Contain("failed to load", "the failure must surface an explicit error, not a silent skip");

        var get = async () => await store.GetAsync("key");
        await get.Should().ThrowAsync<InvalidOperationException>();
        var set = async () => await store.SetAsync("key", "value");
        await set.Should().ThrowAsync<InvalidOperationException>();
        var remove = async () => await store.RemoveAsync("key");
        await remove.Should().ThrowAsync<InvalidOperationException>();
        var list = () => store.ListKeysAsync();
        await list.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task InitializeAsync_MissingStoreFile_KeepsTheHappyPathRoundtripUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The DPAPI store itself is Windows-only; the failure matrix below
            // runs on any platform, this roundtrip needs ProtectedData.
            return;
        }

        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");

            var fresh = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            var init = async () => await fresh.InitializeAsync();
            await init.Should().NotThrowAsync("a missing store file bootstraps an empty, healthy store");

            await fresh.SetAsync("happy-key", "happy-value-42");

            var reloaded = new WindowsDpapiSecretStore(NullLogger<WindowsDpapiSecretStore>.Instance, path);
            await reloaded.InitializeAsync();
            (await reloaded.GetAsync("happy-key")).Should().Be("happy-value-42", "the happy path must survive the load-failure work unchanged");

            await reloaded.RemoveAsync("happy-key");
            (await reloaded.GetAsync("happy-key")).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_CorruptJsonStoreFile_FailsLoudlyAndNeverOverwritesTheFile()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            await File.WriteAllTextAsync(path, "{ this is not json at all ]");
            var untouched = File.ReadAllBytes(path);

            await AssertRefusalBehaviorAsync(path);

            File.ReadAllBytes(path).Should().Equal(untouched, "a failed load must never overwrite the store file");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_TruncatedJsonStoreFile_FailsLoudlyAndNeverOverwritesTheFile()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            await File.WriteAllTextAsync(path, "{\"good-key\": \"AAAA");
            var untouched = File.ReadAllBytes(path);

            await AssertRefusalBehaviorAsync(path);

            File.ReadAllBytes(path).Should().Equal(untouched, "a failed load must never overwrite the store file");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_InvalidBase64Values_FailLoudlyAndNeverOverwriteTheFile()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            await File.WriteAllTextAsync(path, "{\"good-key\": \"AAAA\", \"bad-key\": \"###not-base64###\"}");
            var untouched = File.ReadAllBytes(path);

            await AssertRefusalBehaviorAsync(path);

            File.ReadAllBytes(path).Should().Equal(untouched, "a failed load must never overwrite the store file");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_LockedStoreFile_FailsLoudlyAndLeavesBytesUntouched()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.dat");
            await File.WriteAllTextAsync(path, "{\"good-key\": \"AAAA\"}");
            var untouched = File.ReadAllBytes(path);

            await using (var hold = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                await AssertRefusalBehaviorAsync(path);
            }

            File.ReadAllBytes(path).Should().Equal(untouched, "a failed load must never overwrite the store file");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
