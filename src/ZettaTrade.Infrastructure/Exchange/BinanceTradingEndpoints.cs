namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Binance venue trading surface (T-193): an
/// immutable value object from the registration seam. <see cref="Testnet"/>
/// is the shipped pair — the venue trading surface of this task is the
/// testnet venue BY DESIGN, so the shipped constants are testnet hosts and
/// there is no production trading pair to fall back to (trading-safety: a
/// real-money surface must never be reachable "by default"). The value is
/// re-hosted by the adapter, never mutated.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the spot testnet REST API, no trailing slash.</param>
/// <param name="PrivateWsUrl">
/// WebSocket base of the spot testnet user-data stream (the authenticated
/// order channel): the adapter composes the live URL as
/// <c>{PrivateWsUrl}/{listenKey}</c> at session start — the listen key is a
/// per-session REST fact, never a shipped constant.
/// </param>
public sealed record BinanceTradingEndpoints(string RestBaseUrl, string PrivateWsUrl)
{
    /// <summary>Testnet host pair used when nothing else is provided (the only shipped pair).</summary>
    public static readonly BinanceTradingEndpoints Testnet = new(
        BinanceTradingAdapter.DefaultRestBaseUrl,
        BinanceTradingAdapter.DefaultPrivateWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {PrivateWsUrl}";
}
