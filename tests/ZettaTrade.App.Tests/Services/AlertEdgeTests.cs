namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The alert edge's exact-semantics facts (T-096): Above and Below are
/// STRICT comparisons (a tick exactly at the target satisfies neither),
/// At is EXACT decimal equality (no tolerance in either direction — wire
/// prices are tick-exact decimals), the percent move's equality boundary
/// belongs to the alert (a move of exactly the threshold fires), and every
/// unseen value is a thrown argument fault, never a substituted default.
/// </summary>
public sealed class AlertEdgeTests
{
    /// <summary>The edge matrix's shared target (symbol-free: AlertEdge is pure).</summary>
    private const decimal Target = 110m;

    private const decimal NearAtUpper = 110.00000001m;

    private const decimal NearAtLower = 109.99999999m;

    [Fact]
    public void AboveEdge_StrictlyGreaterOnly() =>
        new[]
        {
            (110m, false),
            (NearAtLower, false),
            (NearAtUpper, true),
            (250m, true),
        }
        .Select(tick => AlertEdge.IsLevelHit(AlertTrigger.Above, tick.Item1, Target))
        .Should().Equal([false, false, true, true], "Above is a STRICTLY-greater edge");

    [Fact]
    public void BelowEdge_StrictlyLessOnly() =>
        new[]
        {
            (110m, false),
            (NearAtUpper, false),
            (NearAtLower, true),
            (90m, true),
        }
        .Select(tick => AlertEdge.IsLevelHit(AlertTrigger.Below, tick.Item1, Target))
        .Should().Equal([false, false, true, true], "Below is a STRICTLY-less edge");

    [Fact]
    public void AtEdge_ExactEqualityOnly() =>
        new[]
        {
            (110m, true),
            (NearAtLower, false),
            (NearAtUpper, false),
        }
        .Select(tick => AlertEdge.IsLevelHit(AlertTrigger.At, tick.Item1, Target))
        .Should().Equal([true, false, false], "At is EXACT decimal equality — no tolerance");

    [Fact]
    public void UnknownTrigger_IsAnArgumentFault_NeverADefault() =>
        FluentActions
            .Invoking(() => AlertEdge.IsLevelHit((AlertTrigger)999, 110m, Target))
            .Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*no known edge*");

    [Fact]
    public void SignedMovePercent_IsSignedAgainstTheReference_FaultsOnNonPositiveReference()
    {
        AlertEdge.SignedMovePercent(100m, 110m).Should().Be(10m, "a rise is positive");
        AlertEdge.SignedMovePercent(100m, 90m).Should().Be(-10m, "a fall is negative");
        AlertEdge.SignedMovePercent(100m, 100m).Should().Be(0m, "no move is zero");

        FluentActions
            .Invoking(() => AlertEdge.SignedMovePercent(0m, 110m))
            .Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*strictly positive*");
    }

    [Fact]
    public void MoveThreshold_ExactOrGreater_Fires() =>
        new[]
        {
            (100m, 110m, 10m, true),
            (100m, 109.9999m, 10m, false),
            (100m, 90m, 10m, true),
            (100m, 110.0001m, 10m, true),
            (100m, 100m, 10m, false),
        }
        .Select(sample => AlertEdge.MoveMeetsThreshold(sample.Item1, sample.Item2, sample.Item3))
        .Should().Equal([true, false, true, true, false], "the equality boundary belongs to the alert");

    [Fact]
    public void MoveThreshold_NonPositiveThreshold_IsAnArgumentFault() =>
        FluentActions
            .Invoking(() => AlertEdge.MoveMeetsThreshold(100m, 110m, 0m))
            .Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*strictly positive*");
}
