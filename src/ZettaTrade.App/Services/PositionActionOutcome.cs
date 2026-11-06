namespace ZettaTrade.App.Services;

/// <summary>
/// Terminal state of one position action attempt (close, partial close or
/// reverse): what the attempt resolved to, driving which line the panel shows.
/// </summary>
public enum PositionActionOutcome
{
    /// <summary>Every intended order was placed (the reverse's two legs included).</summary>
    Placed,

    /// <summary>The user refused the confirmation; nothing was placed and nothing is reported.</summary>
    Declined,

    /// <summary>Nothing, or not everything, was placed for a stated localized reason.</summary>
    Failed,
}
