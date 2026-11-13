namespace ZettaTrade.AdapterKit.Tests.Harness;

using System.Collections.Immutable;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The kit's pairs-based secret stub: an <see cref="ISecretStore"/> whose
/// answer is an explicit key → value map — a user-keyed adapter's battery
/// hands it the adapter's own secret keys (never a real credential), and an
/// unmapped key reads back empty exactly like a store missing that pair.
/// </summary>
/// <param name="pairs">The key → value map the stub serves.</param>
public sealed class KitSecretStoreStub(IReadOnlyDictionary<string, string> pairs) : ISecretStore
{
    private readonly IReadOnlyDictionary<string, string> _pairs = pairs.ToImmutableDictionary(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_pairs.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
}
