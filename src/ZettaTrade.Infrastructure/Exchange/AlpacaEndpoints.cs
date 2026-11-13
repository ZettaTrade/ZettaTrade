namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Transport endpoints of the Alpaca adapter: an immutable value object from
/// the registration seam. <see cref="Production"/> is the shipped default;
/// the <see cref="ExchangeEndpointRegistry"/> overrides it from the settings
/// file and the adapter re-hosts the value, never mutates it.
/// </summary>
/// <param name="RestBaseUrl">HTTPS base of the market-data REST API (the bars service), no trailing slash.</param>
/// <param name="CatalogRestBaseUrl">HTTPS base of the assets catalog REST API (a distinct host: Alpaca keeps the symbol catalog on its trading-API host, the bars on the data host), no trailing slash.</param>
/// <param name="WsUrl">WebSocket URL of the market-data stream (one authenticated socket: the free tier's IEX feed host with its <c>/v2/iex</c> tail).</param>
public sealed record AlpacaEndpoints(string RestBaseUrl, string CatalogRestBaseUrl, string WsUrl)
{
    /// <summary>Production host triple used when nothing else is configured.</summary>
    public static readonly AlpacaEndpoints Production = new(
        AlpacaAdapter.DefaultRestBaseUrl,
        AlpacaAdapter.DefaultCatalogRestBaseUrl,
        AlpacaAdapter.DefaultWsUrl);

    /// <inheritdoc/>
    public override string ToString() => $"{RestBaseUrl} / {CatalogRestBaseUrl} / {WsUrl}";
}
