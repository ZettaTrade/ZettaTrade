namespace ZettaTrade.Core.Enums;

/// <summary>
/// The side of the position a liquidation event force-closed (T-242). The
/// venue-normalized fact the adapters carry: a venue wires the fact in its
/// own dialect (Bybit's liquidation frame carries the position side
/// directly, Binance's forced order carries the closing order side) and the
/// adapters map both onto this one semantic — the tape never shows the
/// venue's raw dialect.
/// </summary>
public enum LiquidationSide
{
    /// <summary>A long position was force-closed.</summary>
    Long = 0,

    /// <summary>A short position was force-closed.</summary>
    Short = 1,
}
