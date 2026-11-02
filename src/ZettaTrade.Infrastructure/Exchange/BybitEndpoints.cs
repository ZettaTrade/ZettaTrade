namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Bybit adapter: an immutable value object from
/// the registration seam. <see cref="Production"/> is the shipped default;
/// the <see cref="ExchangeEndpointRegistry"/> overrides it from the settings
/// file and the adapter re-hosts the value, never mutates it.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the REST API, no trailing slash.</param>
/// <param name="WsUrl">WebSocket URL of the v5 public spot stream (single multiplexed socket, topic-prefixed frames).</param>
public sealed record BybitEndpoints(string RestBaseUrl, string WsUrl)
{
    /// <summary>Production host pair used when nothing else is configured.</summary>
    public static readonly BybitEndpoints Production = new(
        BybitAdapter.DefaultRestBaseUrl,
        BybitAdapter.DefaultWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {WsUrl}";
}
