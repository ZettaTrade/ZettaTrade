namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// One row of the persistent realized-trade history (SQLite): the PnL
/// engine's own journal of record. Each accounted ledger fill lands here
/// exactly once — with the commission it was ingested with — so an
/// application restart reconstructs the accounting EXACTLY from these rows
/// (the shipped order journal carries no fee data, yet the realized history
/// must stay deterministic across restarts).
/// </summary>
public sealed class PnlRealizedEntry
{
    /// <summary>Exchange identifier the fill belongs to; part of the primary key.</summary>
    public required string ExchangeId { get; set; }

    /// <summary>The fill's order id; part of the primary key (each ledger order is accounted at most once).</summary>
    public required string OrderId { get; set; }

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; set; }

    /// <summary>Fill side.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>Executed quantity (the ledger row's filled quantity).</summary>
    public required decimal Quantity { get; set; }

    /// <summary>Execution price.</summary>
    public required decimal Price { get; set; }

    /// <summary>Per-fill commission in the symbol's quote currency (zero when the caller passed none).</summary>
    public required decimal Commission { get; set; }

    /// <summary>The fill's realized contribution net of its commission.</summary>
    public required decimal RealizedAmount { get; set; }

    /// <summary>What the fill did to the position book.</summary>
    public required PnlFillEffect Effect { get; set; }

    /// <summary>Execution timestamp (the ledger row's creation stamp, UTC).</summary>
    public required DateTimeOffset RealizedAt { get; set; }
}
