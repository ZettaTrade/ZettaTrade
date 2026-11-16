namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the right-margin label stack (T-248): the pass-through when no
/// labels collide, the push-down that keeps the minimum gap, the resolved
/// order returning in the caller's order (the priority survives), the rigid
/// clamp into the plot and the taller-than-plot anchor. Pure facts — the
/// canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartLabelStackTests
{
    /// <summary>Spread-apart labels keep their own positions.</summary>
    [Fact]
    public void Resolve_SpreadApartLabels_PassThrough()
    {
        var resolved = ChartLabelStack.Resolve([10d, 50d], 8d, 0d, 200d);

        resolved.Should().Equal([10d, 50d]);
    }

    /// <summary>
    /// The push-down: three colliding labels sit exactly one gap apart, and
    /// the resolved list returns in the CALLER'S order — a pushed-aside
    /// label never swaps above the one that pushed it.
    /// </summary>
    [Fact]
    public void Resolve_CollidingLabels_PushDownAndReturnInOrder()
    {
        var resolved = ChartLabelStack.Resolve([10d, 13d, 16d], 8d, 0d, 200d);

        resolved.Should().Equal([10d, 18d, 26d]);
    }

    /// <summary>An unsorted input sorts by y and maps back to the caller's positions.</summary>
    [Fact]
    public void Resolve_AnUnsortedInput_MapsBackToTheCallersOrder()
    {
        var resolved = ChartLabelStack.Resolve([50d, 10d], 8d, 0d, 200d);

        resolved.Should().Equal([50d, 10d]);
    }

    /// <summary>
    /// The rigid clamp: a column overflowing the plot's floor slides up as
    /// ONE piece — every push and every gap survives the translation.
    /// </summary>
    [Fact]
    public void Resolve_TheFloorOverflow_SlidesTheColumnRigidly()
    {
        var resolved = ChartLabelStack.Resolve([100d, 108d, 116d], 8d, 90d, 110d);

        resolved.Should().Equal([94d, 102d, 110d]);
    }

    /// <summary>
    /// A column taller than the plot anchors at the ceiling and lets the
    /// tail clip — the caller skips the off-plot labels, the stack never
    /// reorders.
    /// </summary>
    [Fact]
    public void Resolve_TheColumnTallerThanThePlot_AnchorsAtTheCeiling()
    {
        var resolved = ChartLabelStack.Resolve([100d, 108d, 116d, 124d], 8d, 90d, 110d);

        resolved.Should().Equal([90d, 98d, 106d, 114d]);
    }

    /// <summary>An empty input resolves to nothing.</summary>
    [Fact]
    public void Resolve_AnEmptyInput_IsEmpty()
    {
        ChartLabelStack.Resolve([], 8d, 0d, 200d).Should().BeEmpty();
    }

    /// <summary>An inverted plot rejects instead of resolving.</summary>
    [Fact]
    public void Resolve_AnInvertedPlot_Throws()
    {
        var act = () => ChartLabelStack.Resolve([10d], 8d, 100d, 0d);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
