namespace ZettaTrade.Core.Tests.Entities;

using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Xunit;

/// <summary>
/// The ticker's funding-members contract (T-197): the three derivative
/// facts default to null — a spot or stock ticker keeps the honest absent
/// values, never a fabricated zero, and its seven-argument construction
/// sites compile unchanged.
/// </summary>
public sealed class TickerTests
{
    [Fact]
    public void ASpotTicker_KeepsTheFundingMembersHonestNulls()
    {
        var ticker = new Ticker(
            Symbol.Parse("BTC/USDT"),
            50000.5m,
            0m,
            0m,
            123.456m,
            2.04m,
            DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));

        ticker.FundingRate.Should().BeNull("spot carries no funding rate");
        ticker.OpenInterest.Should().BeNull("spot carries no open interest");
        ticker.NextFundingTime.Should().BeNull("spot carries no funding schedule");
    }

    [Fact]
    public void APerpTicker_ServesTheDerivativeFacts()
    {
        var nextFunding = DateTimeOffset.FromUnixTimeMilliseconds(1700000400000);
        var ticker = new Ticker(
            Symbol.Parse("BTC/USDT:USDT"),
            60000m,
            0m,
            0m,
            1m,
            0m,
            DateTimeOffset.FromUnixTimeMilliseconds(1699999999000),
            FundingRate: 0.0001m,
            OpenInterest: 51234.5m,
            NextFundingTime: nextFunding);

        ticker.FundingRate.Should().Be(0.0001m);
        ticker.OpenInterest.Should().Be(51234.5m);
        ticker.NextFundingTime.Should().Be(nextFunding);
    }
}
