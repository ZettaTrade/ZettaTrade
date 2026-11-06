namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;

/// <summary>
/// One resolved outcome of a position action attempt (close, partial close
/// or reverse): the placed orders, the localized result line to show on
/// success, or the localized reason nothing (or not everything) was placed.
/// A refused confirmation is neither a success nor a failure — the outcome
/// distinguishes the declined path so the panel stays silent on an answer
/// the user gave themselves, exactly like the new-order dialog does.
/// </summary>
public sealed record PositionActionResult
{
    /// <summary>The terminal state of the attempt.</summary>
    public required PositionActionOutcome Outcome { get; init; }

    /// <summary>
    /// Orders this attempt placed, in placement order. Empty unless
    /// <see cref="PositionActionOutcome.Placed"/>; on a reverse aborted
    /// after its exit leg it names exactly what did reach the surface.
    /// </summary>
    public IReadOnlyList<Order> PlacedOrders { get; init; } = [];

    /// <summary>Localized line describing the placed work (success path).</summary>
    public string? ResultMessage { get; init; }

    /// <summary>Localized line describing why nothing, or not everything, was placed (failure path).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when the attempt placed its orders and finished cleanly.</summary>
    public bool IsSuccess => Outcome == PositionActionOutcome.Placed;

    /// <summary>A fully placed attempt with its result line.</summary>
    public static PositionActionResult PlacedOk(
        IReadOnlyList<Order> orders,
        string resultMessage) => new()
    {
        Outcome = PositionActionOutcome.Placed,
        PlacedOrders = orders,
        ResultMessage = resultMessage,
    };

    /// <summary>The user refused the confirmation: nothing placed, nothing reported.</summary>
    public static PositionActionResult Declined() => new()
    {
        Outcome = PositionActionOutcome.Declined,
    };

    /// <summary>A failed attempt with its localized reason (and whatever it did place).</summary>
    public static PositionActionResult Fail(
        string errorMessage,
        IReadOnlyList<Order>? placedOrders = null) => new()
    {
        Outcome = PositionActionOutcome.Failed,
        PlacedOrders = placedOrders ?? [],
        ErrorMessage = errorMessage,
    };
}
