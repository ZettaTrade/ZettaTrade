namespace ZettaTrade.App.Services;

/// <summary>
/// The parsed protective-level edits one dialog open hands back (T-187): the
/// stop-loss and take-profit prices the user applied, both optional — a null
/// level means "clear this level", both nulls mean "clear the pair". The
/// dialog only parses and direction-checks; the pipeline itself (the fresh
/// state re-read, the risk gates, the one confirmation, the cancel+place
/// rewrite) runs in <see cref="ProtectiveLevelsService"/>.
/// </summary>
public sealed record ProtectiveLevelsEdits
{
    /// <summary>The stop-loss price, or null when the level is cleared.</summary>
    public decimal? StopLoss { get; init; }

    /// <summary>The take-profit price, or null when the level is cleared.</summary>
    public decimal? TakeProfit { get; init; }
}
