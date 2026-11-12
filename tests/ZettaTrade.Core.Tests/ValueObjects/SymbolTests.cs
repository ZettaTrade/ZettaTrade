namespace ZettaTrade.Core.Tests.ValueObjects;

using System;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Xunit;

/// <summary>
/// Tests for <see cref="Symbol"/> parsing, especially exchange-format round-trips
/// used by live market data (contract enforced for Binance event payloads).
/// </summary>
public sealed class SymbolTests
{
    [Fact]
    public void TryParseExchangeFormat_BinanceConcatenated_CryptoSymbol_ShouldProduceNormalizedForm()
    {
        // Act
        var parsed = Symbol.TryParseExchangeFormat("BTCUSDT", "binance", out var symbol);

        // Assert
        parsed.Should().BeTrue();
        symbol.Should().NotBeNull();
        symbol!.BaseAsset.Should().Be("BTC");
        symbol.QuoteAsset.Should().Be("USDT");
        symbol.AssetClass.Should().Be(AssetClass.Crypto);
        symbol.Normalized.Should().Be("BTC/USDT");
    }

    [Fact]
    public void TryParseExchangeFormat_RoundTripThroughGetExchangeFormat_ShouldBeStable()
    {
        // Arrange
        var symbol = new Symbol("ETH", "USDT", AssetClass.Crypto);

        // Act
        var exchangeFormat = symbol.GetExchangeFormat("binance");
        var parsed = Symbol.TryParseExchangeFormat(exchangeFormat, "binance", out var restored);

        // Assert: a symbol that was published and re-received must keep the same normalized key,
        // otherwise subscriptions stop matching after a server-side event.
        parsed.Should().BeTrue();
        restored!.Normalized.Should().Be(symbol.Normalized);
    }

    [Fact]
    public void TryParseExchangeFormat_EmptyInput_ShouldFail()
    {
        Symbol.TryParseExchangeFormat("", "binance", out _).Should().BeFalse();
        Symbol.TryParseExchangeFormat(" ", "binance", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseExchangeFormat_QuoteOnlyString_ShouldFail()
    {
        Symbol.TryParseExchangeFormat("USDT", "binance", out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_NormalizedCryptoPair_ShouldKeepQuoteAsset()
    {
        // Act
        var symbol = Symbol.Parse("BTC/USDT");

        // Assert
        symbol.AssetClass.Should().Be(AssetClass.Crypto);
        symbol.BaseAsset.Should().Be("BTC");
        symbol.QuoteAsset.Should().Be("USDT");
        symbol.Normalized.Should().Be("BTC/USDT");
    }

    [Fact]
    public void Parse_SingleTickerName_ShouldFallBackToStockClass()
    {
        // Parse without a quote asset is by contract a stock ticker.
        var symbol = Symbol.Parse("AAPL");

        symbol.AssetClass.Should().Be(AssetClass.Stock);
        symbol.QuoteAsset.Should().BeNull();
        symbol.Normalized.Should().Be("AAPL");
    }

    #region Perpetual symbols (T-197)

    [Fact]
    public void Parse_SettleMarkedPair_ParsesThePerpetual()
    {
        var symbol = Symbol.Parse("BTC/USDT:USDT");

        symbol.AssetClass.Should().Be(AssetClass.Crypto);
        symbol.BaseAsset.Should().Be("BTC");
        symbol.QuoteAsset.Should().Be("USDT");
        symbol.SettleAsset.Should().Be("USDT");
        symbol.IsPerpetual.Should().BeTrue("the settlement asset carries the category");
        symbol.Normalized.Should().Be("BTC/USDT:USDT");
    }

    [Fact]
    public void Parse_SettleMarkedPair_RoundTripsThroughItsNormalizedText()
    {
        Symbol.Parse(Symbol.Parse("ETH/USDT:USDT").Normalized).Normalized.Should().Be("ETH/USDT:USDT");
    }

    [Fact]
    public void Parse_SpotPair_IsNeverAPerpetual()
    {
        Symbol.Parse("BTC/USDT").IsPerpetual.Should().BeFalse();
        Symbol.Parse("AAPL").IsPerpetual.Should().BeFalse();
    }

    [Theory]
    [InlineData("BTC/USDT:")]
    [InlineData("BTC/:USDT")]
    [InlineData("BTC:")]
    [InlineData("BTC:USDT")]
    [InlineData("BTC/USDT:USDT:USDT")]
    public void Parse_MalformedSettleMarks_ThrowInsteadOfDefaulting(string malformed)
    {
        FluentActions.Invoking(() => Symbol.Parse(malformed))
            .Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Ctor_SettleAssetWithoutQuoteAsset_Throws()
    {
        FluentActions.Invoking(() => new Symbol("BTC", null, AssetClass.Crypto, "USDT"))
            .Should().ThrowExactly<ArgumentException>()
            .WithParameterName("settleAsset");
    }

    [Fact]
    public void GetExchangeFormat_PerpetualBybitWire_CarriesTheConcatenatedPair()
    {
        // The v5 category rides the request, not the symbol text: the perp's
        // wire text is the same concatenated pair its spot twin speaks.
        var perp = Symbol.Parse("BTC/USDT:USDT");

        perp.GetExchangeFormat("bybit").Should().Be("BTCUSDT");
    }

    #endregion
}
