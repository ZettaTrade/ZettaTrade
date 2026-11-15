namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The cascade aggregator's goldens (T-242): the chain rule groups a run (a
/// forced order within 30 s of the run's previous one), the notional bound
/// qualifies it (250 000 reached), a run under the bound stays the tape's
/// ordinary rows (never a badge), one run never chains across contracts and
/// the window state's openness rides the run's last event's freshness — a
/// run gone quiet past the window is closed history and the badge clears.
/// </summary>
public sealed class LiquidationCascadeAggregatorTests
{
    #region Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    private static readonly Symbol EthUsdt = Symbol.Parse("ETH/USDT");

    private static readonly DateTimeOffset Base = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Window = LiquidationCascadeAggregator.DefaultCascadeWindow;

    private const decimal Threshold = LiquidationCascadeAggregator.DefaultCascadeNotionalThreshold;

    #endregion

    #region Test Data

    private static LiquidationEvent Ev(
        Symbol symbol,
        LiquidationSide side,
        decimal price,
        decimal quantity,
        int offsetSeconds) => new()
        {
            Symbol = symbol,
            LiquidatedSide = side,
            Price = new Price(price),
            Quantity = new Quantity(quantity),
            Timestamp = Base.AddSeconds(offsetSeconds),
        };

    #endregion

    #region Qualification

    [Fact]
    public void ASingleLargeForcedOrder_Qualifies()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            [Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 6m, 0)],
            Window,
            Threshold);

        cascades.Should().ContainSingle("one forced order of 300 000 notional is over the bound");
        cascades[0].Symbol.Should().Be(BtcUsdt);
        cascades[0].OrderCount.Should().Be(1);
        cascades[0].TotalNotional.Should().Be(300_000m);
        cascades[0].StartedAt.Should().Be(Base);
        cascades[0].EndedAt.Should().Be(Base);
    }

    [Fact]
    public void TheBound_IsInclusive()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            [Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 5m, 0)],
            Window,
            Threshold);

        cascades.Should().ContainSingle("a run AT the bound qualifies (the bound is a floor, not an exclusive one)");
        cascades[0].TotalNotional.Should().Be(Threshold);
    }

    [Fact]
    public void ARunUnderTheBound_IsTheTapesOrdinaryRows()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, 0),
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, 5),
            },
            Window,
            Threshold);

        cascades.Should().BeEmpty("200 000 of chained forced orders is under the bound — ordinary rows, never a cascade");
    }

    [Fact]
    public void AnEmptyList_YieldsNoCascades()
    {
        var cascades = LiquidationCascadeAggregator.Group([], Window, Threshold);

        cascades.Should().BeEmpty();
    }

    #endregion

    #region Chain Rule

    [Fact]
    public void TheChainRule_GroupsTheRun_AndSplitsAcrossTheGap()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, 0),
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, 10),
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, 20),
                Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 6m, 60),
            },
            Window,
            Threshold);

        cascades.Should().HaveCount(2, "the 40 s gap after the 20 s event splits the run");
        cascades[0].OrderCount.Should().Be(3);
        cascades[0].StartedAt.Should().Be(Base);
        cascades[0].EndedAt.Should().Be(Base.AddSeconds(20));
        cascades[1].OrderCount.Should().Be(1);
        cascades[1].StartedAt.Should().Be(Base.AddSeconds(60));
    }

    [Fact]
    public void ARunNeverChainsAcrossContracts()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 6m, 0),
                Ev(EthUsdt, LiquidationSide.Short, 50_000m, 6m, 5),
            },
            Window,
            Threshold);

        cascades.Should().HaveCount(2, "one run belongs to ONE contract; two contracts are two runs");
        cascades.Select(cascade => cascade.Symbol).Should().Equal(new[] { BtcUsdt, EthUsdt }, "the groups come out in first-seen order");
    }

    #endregion

    #region Dominant Side

    [Fact]
    public void TheDominantSide_CarriesTheRun()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 3m, 0),
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2.4m, 5),
                Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 1.2m, 10),
            },
            Window,
            Threshold);

        cascades.Should().ContainSingle();
        cascades[0].TotalNotional.Should().Be(330_000m);
        cascades[0].DominantSide.Should().Be(LiquidationSide.Long, "the longs' 270 000 carries the run");
    }

    [Fact]
    public void AShortCarriedRun_IsShort()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 3m, 0),
                Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 2.4m, 5),
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 1.2m, 10),
            },
            Window,
            Threshold);

        cascades.Should().ContainSingle();
        cascades[0].DominantSide.Should().Be(LiquidationSide.Short);
    }

    [Fact]
    public void ATiedRun_TiesTowardLong()
    {
        var cascades = LiquidationCascadeAggregator.Group(
            new[]
            {
                Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2.5m, 0),
                Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 2.5m, 5),
            },
            Window,
            Threshold);

        cascades.Should().ContainSingle();
        cascades[0].DominantSide.Should().Be(LiquidationSide.Long, "an equal split names the long the run's head");
    }

    #endregion

    #region Validation

    [Fact]
    public void ANegativeWindow_IsRejected()
    {
        var act = () => LiquidationCascadeAggregator.Group([], TimeSpan.FromSeconds(-1), Threshold);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("window");
    }

    [Fact]
    public void ANegativeThreshold_IsRejected()
    {
        var act = () => LiquidationCascadeAggregator.Group([], Window, -1m);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("threshold");
    }

    #endregion

    #region Window State

    [Fact]
    public void TheOpenCascade_StaysWhileTheRunIsFresh()
    {
        // One qualifying run that ended 10 s before the asOf: still inside
        // the chain window — the run could still be growing.
        var events = new[]
        {
            Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 3m, -20),
            Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 3m, -10),
        };

        var state = LiquidationCascadeAggregator.WindowState(events, Base, Window, Threshold);

        state.OpenCascade.Should().NotBeNull("the run's tail is 10 s old — inside the chain window");
        state.OpenCascade!.OrderCount.Should().Be(2);
        state.OpenCascade.TotalNotional.Should().Be(300_000m);
    }

    [Fact]
    public void AQuietRun_ClearsTheBadge()
    {
        var events = new[] { Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 6m, -31) };

        var state = LiquidationCascadeAggregator.WindowState(events, Base, Window, Threshold);

        state.OpenCascade.Should().BeNull("the run's tail went quiet past the chain window — closed history, the badge clears");
    }

    [Fact]
    public void TheWindowCarriesTheRecentFacts()
    {
        var events = new[]
        {
            Ev(BtcUsdt, LiquidationSide.Long, 50_000m, 2m, -20),
            Ev(BtcUsdt, LiquidationSide.Short, 50_000m, 3m, -5),
        };

        var state = LiquidationCascadeAggregator.WindowState(events, Base, Window, Threshold);

        state.Recent.Should().Equal(events, "the window's recent list IS the served ring");
        state.RecentCount.Should().Be(2);
        state.RecentNotional.Should().Be(250_000m, "the ring's summed served notional");
    }

    [Fact]
    public void AnEmptyWindow_IsTheHonestNoFacts()
    {
        var state = LiquidationCascadeAggregator.WindowState([], Base, Window, Threshold);

        state.Recent.Should().BeEmpty();
        state.RecentCount.Should().Be(0);
        state.RecentNotional.Should().Be(0m);
        state.OpenCascade.Should().BeNull();
    }

    #endregion
}
