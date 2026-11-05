namespace ZettaTrade.Infrastructure.Tests.Secrets;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Secrets;

/// <summary>
/// T-049 store-rule tests for the encrypted fallback file store:
/// <list type="bullet">
/// <item>BUG-041: a truncated store file (too small to even carry a nonce and
/// a tag) is corrupted content, never an "empty store": initialization fails
/// loudly, the file stays byte-identical and no <c>SetAsync</c> overwrites it;
/// the missing FILE (the genuinely empty store) keeps bootstrapping the old
/// happy path.</item>
/// </list>
/// </summary>
public sealed class EncryptedFileSecretStoreRulesTests
{
    private const string MasterPassphrase = "t-049-master-passphrase";

    private static string NewStoreDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t049-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task MissingStoreFile_KeepsTheHappyPathRoundtripUnchanged()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.enc");
            var store = new EncryptedFileSecretStore(NullLogger<EncryptedFileSecretStore>.Instance, path);

            var init = async () => await store.InitializeAsync(MasterPassphrase);
            await init.Should().NotThrowAsync("a missing store file bootstraps an empty, healthy store");

            await store.SetAsync("key", "value-42");
            (await store.GetAsync("key")).Should().Be("value-42");

            var reloaded = new EncryptedFileSecretStore(NullLogger<EncryptedFileSecretStore>.Instance, path);
            await reloaded.InitializeAsync(MasterPassphrase);
            (await reloaded.GetAsync("key")).Should().Be("value-42", "the secret survives a restart");

            (await reloaded.ListKeysAsync()).Should().Equal("key");
            await reloaded.RemoveAsync("key");
            (await reloaded.GetAsync("key")).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatedStoreFile_FailsLoudlyAndIsNeverOverwritten()
    {
        var directory = NewStoreDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.enc");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            var untouched = File.ReadAllBytes(path);

            var store = new EncryptedFileSecretStore(NullLogger<EncryptedFileSecretStore>.Instance, path);
            var init = async () => await store.InitializeAsync(MasterPassphrase);
            var thrown = await init.Should().ThrowAsync<InvalidOperationException>();
            thrown.And.Message.Should().Contain("truncated", "a too-short file is corrupted content, not an empty store");

            File.ReadAllBytes(path).Should().Equal(untouched, "a failed load never overwrites the store file");

            // The store stays uninitialized: even a write refuses instead of
            // overwriting the unreadable content with a fresh file.
            var set = () => store.SetAsync("key", "value");
            await set.Should().ThrowAsync<InvalidOperationException>();
            File.ReadAllBytes(path).Should().Equal(untouched);

            // A re-initialization keeps refusing on the same file.
            var reinitialized = new EncryptedFileSecretStore(NullLogger<EncryptedFileSecretStore>.Instance, path);
            var reinit = async () => await reinitialized.InitializeAsync(MasterPassphrase);
            await reinit.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
