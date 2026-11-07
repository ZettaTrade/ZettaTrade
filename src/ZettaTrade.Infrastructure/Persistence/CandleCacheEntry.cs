namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Enums;

/// <summary>
/// One stored candlestick row (SQLite): the (adapter, symbol, interval)
/// series keyed by open time. The row mirrors the Core <c>Bar</c> fields
/// exactly — nothing else (no fetch bookkeeping, no gap markers) gets a
/// column; deep-history stitching and gap repair are T-086 territory, not
/// this table's concern.
/// </summary>
public sealed class CandleCacheEntry
{
    /// <summary>Adapter identifier the series was served by; part of the primary key.</summary>
    public required string AdapterId { get; set; }

    /// <summary>Normalized symbol (BASE/QUOTE); part of the primary key.</summary>
    public required string Symbol { get; set; }

    /// <summary>Candle interval; part of the primary key.</summary>
    public required TimeFrame TimeFrame { get; set; }

    /// <summary>Bar open time in UTC; part of the primary key — a series
    /// upserts by open time, so a re-served bar rewrites its own row and
    /// never duplicates.</summary>
    public required DateTimeOffset OpenTime { get; set; }

    /// <summary>Candle open price.</summary>
    public required decimal Open { get; set; }

    /// <summary>Candle high price.</summary>
    public required decimal High { get; set; }

    /// <summary>Candle low price.</summary>
    public required decimal Low { get; set; }

    /// <summary>Candle close price.</summary>
    public required decimal Close { get; set; }

    /// <summary>Candle traded volume.</summary>
    public required decimal Volume { get; set; }
}
