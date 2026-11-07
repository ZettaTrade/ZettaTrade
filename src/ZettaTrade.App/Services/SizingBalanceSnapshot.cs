namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One captured balance moment for the position sizing helper: the amounts
/// the trading face served for the ticket instrument's base and quote
/// assets at one point in time, kept as-is afterwards (a snapshot is never
/// silently refreshed — only a deliberate capture replaces it). An asset
/// the source did not serve stays an explicit absence (<c>null</c>),
/// never a fabricated zero the presets could act on; every proposal the
/// helper makes rides exactly this captured moment and says so.
/// </summary>
/// <param name="Symbol">The instrument the snapshot belongs to; a snapshot never outlives its instrument.</param>
/// <param name="BaseAsset">Base asset code of the instrument (e.g. "BTC").</param>
/// <param name="QuoteAsset">Quote asset code of the instrument (e.g. "USDT").</param>
/// <param name="QuoteAvailable">Served quote-asset amount, or null when the source did not carry it.</param>
/// <param name="BaseAvailable">Served base-asset amount, or null when the source did not carry it.</param>
/// <param name="CapturedAt">When the capture landed (the plain staleness display the row shows).</param>
public sealed record SizingBalanceSnapshot(
    Symbol Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal? QuoteAvailable,
    decimal? BaseAvailable,
    DateTimeOffset CapturedAt);
