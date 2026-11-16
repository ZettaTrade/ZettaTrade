namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Binance adapter: an immutable value object
/// from the registration seam. <see cref="Production"/> is the shipped
/// default; the <see cref="ExchangeEndpointRegistry"/> overrides it from
/// the settings file and the adapter re-hosts the value, never mutates it.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the REST API, no trailing slash.</param>
/// <param name="WsUrl">WebSocket URL of the combined stream endpoint (the {"stream","data"} envelope adapter's message handler expects — a raw /ws frame host would drop depth frames silently).</param>
/// <param name="IsTestnet">Venue lane identity: true when this record's endpoints serve a testnet venue (the trading-mode chip's mapping reads it; production endpoints stay false).</param>
/// <param name="FuturesWsUrl">WebSocket URL of the futures combined-stream host — the venue's derivatives wire the forced-liquidation stream rides (T-242). Optional: the shipped default is the production futures host, and a loopback rig overrides it the same way <paramref name="WsUrl"/> is.</param>
/// <param name="FuturesRestBaseUrl">HTTPS base of the futures REST host — the derivatives REST the leverage-bracket table rides (T-243). Optional: the shipped default is the production futures REST host, and a loopback rig overrides it the same way <paramref name="RestBaseUrl"/> is.</param>
public sealed record BinanceEndpoints(
    string RestBaseUrl,
    string WsUrl,
    bool IsTestnet = false,
    string? FuturesWsUrl = null,
    string? FuturesRestBaseUrl = null)
{
    /// <summary>Production host pair used when nothing else is configured.</summary>
    public static readonly BinanceEndpoints Production = new(
        BinanceAdapter.DefaultRestBaseUrl,
        BinanceAdapter.DefaultWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {WsUrl}";
}
