namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Export;

/// <summary>
/// One realized-trade display row of the trade statistics window (T-211):
/// the ledger row's booked outcome as precomputed localized strings, plus
/// the row's journal identity and annotations - the edit flow targets the
/// closing journal row through (ExchangeId, ClosingOrderId), and the note
/// and tags cells show exactly what the journal kept at scan time. The rows
/// are immutable: an applied edit replaces the row instance (a record
/// <c>with</c> the fresh annotations).
/// </summary>
public sealed record TradeRealizedRowViewModel
{
    /// <summary>Build one display row from a ledger row. The journal stores
    /// UTC; display follows the local wall clock, like the other panels.</summary>
    public static TradeRealizedRowViewModel From(StatementRealizedTrade trade) => new()
    {
        ExchangeId = trade.ExchangeId,
        OrderId = trade.ClosingOrderId,
        ClosedAt = trade.ClosedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
        Exchange = trade.ExchangeId,
        Symbol = trade.Symbol,
        Side = trade.ClosingSide,
        IsBuySide = trade.ClosingSide is "Buy",
        IsSellSide = trade.ClosingSide is "Sell",
        Quantity = trade.Quantity.ToString("0.0########", CultureInfo.InvariantCulture),
        EntryPrice = trade.EntryPrice.ToString("0.0########", CultureInfo.InvariantCulture),
        ExitPrice = trade.ExitPrice.ToString("0.0########", CultureInfo.InvariantCulture),
        Pnl = trade.RealizedPnl.ToString("F4", CultureInfo.InvariantCulture),
        IsWinning = trade.RealizedPnl > 0,
        IsLosing = trade.RealizedPnl < 0,
        Note = trade.Note,
        Tags = trade.Tags,
    };

    /// <summary>Closing journal row's exchange; the annotation write's key half.</summary>
    public required string ExchangeId { get; init; }

    /// <summary>Closing journal row's order id; the annotation write's key half.</summary>
    public required string OrderId { get; init; }

    /// <summary>When the trade closed (local wall clock).</summary>
    public required string ClosedAt { get; init; }

    /// <summary>Venue the trade rode.</summary>
    public required string Exchange { get; init; }

    /// <summary>Traded symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Localized closing side.</summary>
    public required string Side { get; init; }

    /// <summary>Whether the closing side was a Buy (the side chip's state).</summary>
    public required bool IsBuySide { get; init; }

    /// <summary>Whether the closing side was a Sell (the side chip's state).</summary>
    public required bool IsSellSide { get; init; }

    /// <summary>Closed quantity (the paired size).</summary>
    public required string Quantity { get; init; }

    /// <summary>Position's weighted-average entry price.</summary>
    public required string EntryPrice { get; init; }

    /// <summary>Closing fill's price.</summary>
    public required string ExitPrice { get; init; }

    /// <summary>Realized PnL in the symbol's quote currency (invariant form).</summary>
    public required string Pnl { get; init; }

    /// <summary>Whether the trade realized a profit (the PnL cell's state).</summary>
    public required bool IsWinning { get; init; }

    /// <summary>Whether the trade realized a loss (the PnL cell's state).</summary>
    public required bool IsLosing { get; init; }

    /// <summary>The journal row's note; null is the honest no-note.</summary>
    public required string? Note { get; init; }

    /// <summary>The journal row's canonical comma-joined tag list; null is the honest no-tags.</summary>
    public required string? Tags { get; init; }
}
