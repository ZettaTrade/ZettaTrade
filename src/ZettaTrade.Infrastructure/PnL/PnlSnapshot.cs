namespace ZettaTrade.Infrastructure.PnL;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One position account as the engine's state feed presents it: a fully
/// immutable snapshot row, safe to hand to any consumer. Signed quantity is
/// the honest side carrier (positive = long, negative = short, zero =
/// flat); realized and unrealized PnL are expressed in the symbol's quote
/// currency as parsed from the ledger symbol (empty for symbols without a
/// quote asset — the engine never fabricates a currency).
/// </summary>
/// <remarks>
/// Unrealized PnL is derived from the latest observed market price for the
/// SYMBOL (shared across exchanges — the facade serves the active
/// exchange's feed). Until a price has been observed, it is zero and
/// <see cref="LastPrice"/> is null: a snapshot never invents a price.
/// </remarks>
public sealed record PnlPositionSnapshot
{
    /// <summary>Exchange identifier the account belongs to.</summary>
    public required string ExchangeId { get; init; }

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Quote currency of the symbol (empty when the symbol carries none).</summary>
    public required string QuoteCurrency { get; init; }

    /// <summary>Positive = long, negative = short, zero = flat.</summary>
    public required decimal SignedQuantity { get; init; }

    /// <summary>Quantity-weighted average entry price of the open position (zero when flat).</summary>
    public required decimal AverageEntryPrice { get; init; }

    /// <summary>Running realized PnL net of commissions, in the quote currency.</summary>
    public required Money RealizedPnl { get; init; }

    /// <summary>The latest market price observed for the symbol, if any.</summary>
    public required decimal? LastPrice { get; init; }

    /// <summary>
    /// Unrealized PnL at <see cref="LastPrice"/> (zero until a price is
    /// observed or when the account is flat).
    /// </summary>
    public required Money UnrealizedPnl { get; init; }

    /// <summary>True when the account holds nothing; drivers for empty-state UI.</summary>
    public bool IsFlat => SignedQuantity == 0m;
}

/// <summary>
/// The complete state the engine feeds its consumers: every position
/// account (including flat ones with a realized history), ordered by
/// (exchange, symbol) so the feed is deterministic across reconstructions.
/// </summary>
public sealed record PnlSnapshot
{
    /// <summary>The position accounts, ordered deterministically.</summary>
    public required IReadOnlyList<PnlPositionSnapshot> Positions { get; init; }

    /// <summary>The snapshot before anything is accounted.</summary>
    public static PnlSnapshot Empty { get; } = new() { Positions = [] };
}
