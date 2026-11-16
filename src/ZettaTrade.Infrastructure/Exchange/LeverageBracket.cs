namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// One leverage bracket of a perpetual contract (T-243): the tier of the
/// venue's public risk table a position's notional falls into — the maximum
/// leverage the tier allows and the maintenance-margin rate the tier
/// charges. The tiers ascend by notional: a bracket's floor is the
/// previous tier's cap (the first tier floors at zero), so a position's
/// notional picks exactly one bracket.
/// </summary>
/// <param name="NotionalFloor">The inclusive notional floor of the tier (zero for the first).</param>
/// <param name="NotionalCap">The exclusive notional cap of the tier (the venue's own served bound).</param>
/// <param name="MaxLeverage">The maximum leverage the tier allows (the venue's own served bound).</param>
/// <param name="MaintenanceMarginRate">The tier's maintenance-margin rate as a fraction (0.005 = 0.5%).</param>
public readonly record struct LeverageBracket(
    decimal NotionalFloor,
    decimal NotionalCap,
    decimal MaxLeverage,
    decimal MaintenanceMarginRate);
