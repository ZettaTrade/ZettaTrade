namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One performance-bucket row of the trade-statistics window (T-251): the
/// hourly or weekday slice of the booked trades — the bucket's label, the
/// decided counts and the net realized PnL, all as precomputed invariant
/// strings like the other panels' rows. An empty bucket still renders its
/// label with zeros and a dash (the honest no-trades bucket, never hidden),
/// and the PnL cell tints by the realized sign exactly like the realized
/// rows' cells.
/// </summary>
public sealed record TradeStatsBucketRowViewModel
{
    /// <summary>The bucket's display label (the invariant UTC hour or the localized weekday).</summary>
    public required string Bucket { get; init; }

    /// <summary>Closing trades booked into the bucket.</summary>
    public required string Trades { get; init; }

    /// <summary>Trades that realized a positive PnL.</summary>
    public required string Wins { get; init; }

    /// <summary>Trades that realized a negative PnL.</summary>
    public required string Losses { get; init; }

    /// <summary>The bucket's net realized PnL (invariant form); a dash while nothing booked.</summary>
    public required string Pnl { get; init; }

    /// <summary>Whether the bucket's net PnL is positive (the PnL cell's state).</summary>
    public required bool IsWinning { get; init; }

    /// <summary>Whether the bucket's net PnL is negative (the PnL cell's state).</summary>
    public required bool IsLosing { get; init; }
}
