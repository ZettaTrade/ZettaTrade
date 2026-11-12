namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Bybit adapter: an immutable value object from
/// the registration seam. <see cref="Production"/> is the shipped default;
/// the <see cref="ExchangeEndpointRegistry"/> overrides it from the settings
/// file and the adapter re-hosts the value, never mutates it.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the REST API, no trailing slash. The category rides the request path, so one REST host serves both categories (T-197).</param>
/// <param name="WsUrl">WebSocket URL of the v5 public spot stream (single multiplexed socket, topic-prefixed frames).</param>
/// <param name="IsTestnet">Venue lane identity: true when this record's endpoints serve a testnet venue (the trading-mode chip's mapping reads it; production endpoints stay false).</param>
/// <param name="LinearWsUrl">WebSocket URL of the v5 public linear-perp stream (T-197, a separate socket: Bybit keeps the categories on distinct hosts). Blank falls back to the adapter's own derivation — the spot URL's <c>/spot</c> tail becomes <c>/linear</c>, else the shipped production linear host.</param>
public sealed record BybitEndpoints(string RestBaseUrl, string WsUrl, bool IsTestnet = false, string? LinearWsUrl = null)
{
    /// <summary>Production host pair used when nothing else is configured.</summary>
    public static readonly BybitEndpoints Production = new(
        BybitAdapter.DefaultRestBaseUrl,
        BybitAdapter.DefaultWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {WsUrl}";
}
