namespace ZettaTrade.Backtest.Tests;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The sweep request's composition facts (T-212): the axes validate on
/// their own (defined knob, 1..8 values, each value inside its knob's
/// request bounds), the two axes must name DIFFERENT knobs (a duplicate
/// grid is refused, never collapsed), the template must be a fully valid
/// run request, nothing here defaults or clamps — and
/// <see cref="BacktestSweepRequest.Apply"/> composes one parameterization
/// exactly: both axes' knobs set, every unswept knob riding the template.
/// </summary>
public sealed class BacktestSweepRequestTests
{
    #region Fixture

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>A fully valid template: the demo adapter's H1 series, 128 bars, zero slip.</summary>
    private static BacktestRunRequest Template(
        decimal fraction = BacktestRunRequest.DefaultLongNotionalFraction,
        decimal slippage = BacktestRunRequest.DefaultSlippageValue) => new()
        {
            StrategyId = "sample-cross",
            AdapterId = "demo",
            Symbol = BtcUsdt,
            Interval = TimeFrame.H1,
            Bars = 128,
            Seed = 7,
            LongNotionalFraction = fraction,
            SlippageValue = slippage,
        };

    private static BacktestSweepAxis Axis(
        BacktestSweepParameter parameter,
        params decimal[] values) => new(parameter, values);

    private static BacktestSweepRequest Sweep(
        BacktestRunRequest? template = null,
        BacktestSweepAxis? first = null,
        BacktestSweepAxis? second = null) => new()
        {
            Template = template ?? Template(),
            FirstAxis = first ?? Axis(BacktestSweepParameter.LongNotionalFraction, 0.25m, 0.5m),
            SecondAxis = second ?? Axis(BacktestSweepParameter.SlippageValue, 0m, 1m),
        };

    #endregion Fixture

    [Fact]
    public void Validate_MissingTemplate_IsRefused()
    {
        var sweep = new BacktestSweepRequest
        {
            Template = null!,
            FirstAxis = Axis(BacktestSweepParameter.LongNotionalFraction, 0.5m),
            SecondAxis = Axis(BacktestSweepParameter.SlippageValue, 0m),
        };

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Validate_InvalidTemplate_IsRefused()
    {
        var sweep = Sweep(template: Template() with { Bars = 0 });

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_UndefinedKnob_IsRefused()
    {
        var sweep = Sweep(first: Axis((BacktestSweepParameter)99, 0.5m));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_MissingValuesList_IsRefused()
    {
        var sweep = Sweep(first: Axis(BacktestSweepParameter.LongNotionalFraction, (decimal[])null!));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Validate_EmptyAxis_IsRefused()
    {
        var sweep = Sweep(first: Axis(BacktestSweepParameter.LongNotionalFraction));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_TooLongAxis_IsRefused()
    {
        // Nine values: one past the 1..8 ceiling — the runtime stays predictable.
        var values = new decimal[] { 0.1m, 0.2m, 0.3m, 0.4m, 0.5m, 0.6m, 0.7m, 0.8m, 0.9m };
        var sweep = Sweep(first: Axis(BacktestSweepParameter.LongNotionalFraction, values));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_EightValues_AreAccepted()
    {
        var values = new decimal[] { 0.1m, 0.2m, 0.3m, 0.4m, 0.5m, 0.6m, 0.7m, 0.8m };
        var sweep = Sweep(first: Axis(BacktestSweepParameter.LongNotionalFraction, values));

        sweep.Invoking(subject => subject.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_FractionOutsideUnitInterval_IsRefused()
    {
        foreach (var value in new decimal[] { 0m, -0.25m, 1.0001m, 2m })
        {
            var sweep = Sweep(first: Axis(BacktestSweepParameter.LongNotionalFraction, value));
            sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>(
                because: $"the fraction {value} lies outside (0, 1] and is refused, never clamped");
        }
    }

    [Fact]
    public void Validate_NegativeSlippage_IsRefused()
    {
        var sweep = Sweep(second: Axis(BacktestSweepParameter.SlippageValue, -0.5m));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_DuplicateKnobs_IsRefused()
    {
        var sweep = Sweep(
            first: Axis(BacktestSweepParameter.SlippageValue, 0m, 1m),
            second: Axis(BacktestSweepParameter.SlippageValue, 2m));

        sweep.Invoking(subject => subject.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_FractionAndSlippageAxes_AreAccepted()
    {
        var sweep = Sweep();

        sweep.Invoking(subject => subject.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Apply_SetsBothAxesKnobs_AndRidesTheTemplate()
    {
        var template = Template(fraction: 0.75m, slippage: 2m);
        var sweep = Sweep(
            template: template,
            first: Axis(BacktestSweepParameter.LongNotionalFraction, 0.25m, 0.5m),
            second: Axis(BacktestSweepParameter.SlippageValue, 0m, 1m));

        var row = sweep.Apply(0.5m, 1m);

        // Both knobs come from the cell's pair (the second axis wins any
        // template overlap honestly — the template's own 2m is replaced).
        row.LongNotionalFraction.Should().Be(0.5m);
        row.SlippageValue.Should().Be(1m);

        // Every unswept knob rides the template unchanged: the identity
        // (strategy, venue, series, interval) and the scale (bars, seed,
        // initial balance) are the grid's constant frame.
        row.StrategyId.Should().Be(template.StrategyId);
        row.AdapterId.Should().Be(template.AdapterId);
        row.Symbol.Should().Be(template.Symbol);
        row.Interval.Should().Be(template.Interval);
        row.Bars.Should().Be(template.Bars);
        row.Seed.Should().Be(template.Seed);
        row.InitialQuoteAmount.Should().Be(template.InitialQuoteAmount);
    }

    [Fact]
    public void Apply_ReversedAxes_RoundTripsTheSameCell()
    {
        var forward = Sweep(
            first: Axis(BacktestSweepParameter.LongNotionalFraction, 0.25m),
            second: Axis(BacktestSweepParameter.SlippageValue, 1m));
        var reversed = Sweep(
            first: Axis(BacktestSweepParameter.SlippageValue, 1m),
            second: Axis(BacktestSweepParameter.LongNotionalFraction, 0.25m));

        forward.Apply(0.25m, 1m).Should().Be(reversed.Apply(1m, 0.25m));
    }
}
