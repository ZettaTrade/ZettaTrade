namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the OKX adapter: an immutable value object from
/// the registration seam. <see cref="Production"/> is the shipped default;
/// the <see cref="ExchangeEndpointRegistry"/> overrides it from the settings
/// file and the adapter re-hosts the value, never mutates it.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the REST API, no trailing slash.</param>
/// <param name="WsUrl">WebSocket URL of the v5 public channel stream (single multiplexed socket, <c>{"arg","data"}</c> envelope frames).</param>
/// <param name="IsTestnet">Venue lane identity: true when this record's endpoints serve a testnet venue (the trading-mode chip's mapping reads it; production endpoints stay false).</param>
public sealed record OkxEndpoints(string RestBaseUrl, string WsUrl, bool IsTestnet = false)
{
    /// <summary>Production host pair used when nothing else is configured.</summary>
    public static readonly OkxEndpoints Production = new(
        OkxAdapter.DefaultRestBaseUrl,
        OkxAdapter.DefaultWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {WsUrl}";
}
