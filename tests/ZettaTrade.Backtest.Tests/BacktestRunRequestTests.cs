namespace ZettaTrade.Backtest.Tests;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The request's refuse-not-default matrix (T-113): every malformed shape
/// is refused outright with the specific exception family — nothing is
/// clamped, substituted or blank-tolerated — and a fully valid request
/// passes silently.
/// </summary>
public sealed class BacktestRunRequestTests
{
    /// <summary>One fully valid request every fact mutates.</summary>
    private static BacktestRunRequest Valid() => new()
    {
        StrategyId = "demo-golden-cross",
        AdapterId = "demo",
        Symbol = Symbol.Parse("BTC/USDT"),
        Interval = Core.Enums.TimeFrame.H1,
    };

    [Fact]
    public void Validate_FullyValidRequest_PassesSilently()
    {
        Valid().Validate();
    }

    [Fact]
    public void Validate_UnknownStrategyBlankSlot_IsRefused()
    {
        var request = Valid() with { StrategyId = "   " };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_BlankAdapterId_IsRefused()
    {
        var request = Valid() with { AdapterId = string.Empty };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_MissingSymbol_IsRefused()
    {
        var request = Valid() with { Symbol = null! };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(BacktestRunRequest.MinBars - 1)]
    [InlineData(BacktestRunRequest.MaxBars + 1)]
    public void Validate_BarTargetOutsideBounds_IsRefused(int bars)
    {
        var request = Valid() with { Bars = bars };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(BacktestRunRequest.Bars));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveInitialQuote_IsRefused(decimal quote)
    {
        var request = Valid() with { InitialQuoteAmount = quote };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(BacktestRunRequest.InitialQuoteAmount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    public void Validate_LongFractionOutsideUnitInterval_IsRefused(decimal fraction)
    {
        var request = Valid() with { LongNotionalFraction = fraction };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(BacktestRunRequest.LongNotionalFraction));
    }

    [Fact]
    public void Validate_NegativeSlippage_IsRefused()
    {
        var request = Valid() with { SlippageValue = -0.01m };

        var refuse = () => request.Validate();

        refuse.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(BacktestRunRequest.SlippageValue));
    }

    [Fact]
    public void Validate_BarBoundsAndDefaults_AreTheShippedShape()
    {
        BacktestRunRequest.MinBars.Should().Be(32);
        BacktestRunRequest.MaxBars.Should().Be(100_000);
        BacktestRunRequest.DefaultBars.Should().Be(4_096);
        BacktestRunRequest.DefaultSeed.Should().Be(0);
        BacktestRunRequest.DefaultLongNotionalFraction.Should().Be(1m);
        BacktestRunRequest.DefaultSlippageValue.Should().Be(0m);
    }
}
