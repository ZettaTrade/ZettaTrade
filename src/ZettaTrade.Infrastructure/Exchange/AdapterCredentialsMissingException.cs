namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The standing absence of an adapter's credentials (T-227): the secret
/// store holds no pair the adapter could authenticate with, so every
/// authorized surface refuses with this typed error — the consumers
/// distinguish the standing absence from a real failure and keep the
/// repeated-refusal noise out of the log (the adapter reports the absence
/// once per session; the repeated refusals ride silently).
/// </summary>
public sealed class AdapterCredentialsMissingException : InvalidOperationException
{
    /// <summary>
    /// Create the absence error for one adapter: the missed ISecretStore
    /// keys name themselves.
    /// </summary>
    /// <param name="adapterId">The adapter whose credentials are absent.</param>
    /// <param name="secretKeys">The ISecretStore keys the pair rides.</param>
    public AdapterCredentialsMissingException(string adapterId, params string[] secretKeys)
        : base(BuildMessage(adapterId, secretKeys))
    {
        AdapterId = adapterId;
        SecretKeys = secretKeys;
    }

    /// <summary>The adapter whose credentials are absent.</summary>
    public string AdapterId { get; }

    /// <summary>The ISecretStore keys the pair rides.</summary>
    public IReadOnlyList<string> SecretKeys { get; }

    private static string BuildMessage(string adapterId, string[] secretKeys) =>
        $"{adapterId} credentials are not available in the secret store (keys '{string.Join("', '", secretKeys)}'); " +
        "save the pair through the settings' exchange-credentials section — keys are never passed as command-line arguments";
}
