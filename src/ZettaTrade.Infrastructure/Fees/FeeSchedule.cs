namespace ZettaTrade.Infrastructure.Fees;

/// <summary>
/// One adapter's maker/taker fee schedule, expressed in basis points of the
/// traded notional plus a minimum absolute fee floor (all in the symbol's
/// quote currency). A value object: immutable, comparable by value, and the
/// single authority for turning a notional into a commission.
/// <para>
/// The rounding policy (pinned by goldens): the raw notional × rate/10 000
/// product is rounded HALF-AWAY-FROM-ZERO at <see cref="CommissionDecimals"/>
/// decimal places (the precision trades quote to the eight decimal places
/// used across the journal), and the result is floored to
/// <see cref="MinimumFee"/> — but ONLY when a fee was actually computed: a
/// zero-rate fill never grows a fee out of the floor.
/// </para>
/// </summary>
public sealed record FeeSchedule(decimal MakerRateBps, decimal TakerRateBps, decimal MinimumFee)
{
    /// <summary>Basis points that make up one percent (display conversions).</summary>
    public const decimal BpsPerPercent = 100m;

    /// <summary>Notional ratio of one basis point: 1 bps = 1/10 000 of the notional.</summary>
    public const decimal BpsDenominator = 10_000m;

    /// <summary>
    /// Decimal places of a computed commission (the quote-currency precision
    /// trades and fees carry across the journal and the PnL store).
    /// </summary>
    public const int CommissionDecimals = 8;

    /// <summary>
    /// The commission this schedule charges for one fill of the given role:
    /// max(half-away-from-zero rounded at <see cref="CommissionDecimals"/>,
    /// <see cref="MinimumFee"/>), with the floor applied only when the raw
    /// product was positive. A zero-rate fill stays free — the floor is a
    /// fee floor, never a fabricated cost.
    /// </summary>
    /// <param name="role">Which schedule branch the fill executed on.</param>
    /// <param name="notional">Fill notional in the quote currency (price × quantity).</param>
    /// <returns>The commission in the quote currency.</returns>
    public decimal Commission(FeeRole role, decimal notional)
    {
        var rateBps = RateBpsFor(role);
        var raw = notional * rateBps / BpsDenominator;
        if (raw == 0m)
        {
            return 0m;
        }

        var rounded = Math.Round(raw, CommissionDecimals, MidpointRounding.AwayFromZero);
        return Math.Max(rounded, MinimumFee);
    }

    /// <summary>The schedule branch (in bps) the given role executes on.</summary>
    public decimal RateBpsFor(FeeRole role) =>
        role == FeeRole.Maker ? MakerRateBps : TakerRateBps;
}
