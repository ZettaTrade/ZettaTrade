namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Bybit venue trading surface (T-127): an
/// immutable value object from the registration seam. <see cref="Testnet"/>
/// is the shipped pair — the venue trading surface of this task is the
/// testnet venue BY DESIGN, so the shipped constants are testnet hosts and
/// there is no production trading pair to fall back to (trading-safety: a
/// real-money surface must never be reachable "by default"). The value is
/// re-hosted by the adapter, never mutated.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the v5 REST API, no trailing slash.</param>
/// <param name="PrivateWsUrl">WebSocket URL of the v5 private stream (the authenticated order channel).</param>
public sealed record BybitTradingEndpoints(string RestBaseUrl, string PrivateWsUrl)
{
    /// <summary>Testnet host pair used when nothing else is provided (the only shipped pair).</summary>
    public static readonly BybitTradingEndpoints Testnet = new(
        BybitTradingAdapter.DefaultRestBaseUrl,
        BybitTradingAdapter.DefaultPrivateWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {PrivateWsUrl}";
}
