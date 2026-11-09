namespace ZettaTrade.Infrastructure.Tests.SingleInstance;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The handoff argument grammar's pins (T-104): the allowlist accepts the
/// two flags in both value forms and the protocol strings on the open
/// route, an empty vector stays a valid prompt redirect, and every
/// position outside the allowlist — the order-shaped flags included —
/// is a hard rejection with its own reason, never a defaulted substitute.
/// The grammar walks shape only: symbol and exchange validation is the
/// receiver's semantic layer, never this parser's.
/// </summary>
public sealed class HandoffArgParserTests
{
    #region Goldens

    [Fact]
    public void TheNullVector_ParsesAsTheNoSelectionRedirect()
    {
        HandoffArgParser.Parse(null).Should().Be(HandoffParsed.NoSelection);
    }

    [Fact]
    public void TheEmptyVector_ParsesAsTheNoSelectionRedirect()
    {
        HandoffArgParser.Parse([]).Should().Be(HandoffParsed.NoSelection);
    }

    [Fact]
    public void TheSplitSymbolFlag_CarriesItsRawValue()
    {
        var parsed = HandoffArgParser.Parse(["--symbol", "BTC/USDT"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection("BTC/USDT", null));
    }

    [Fact]
    public void TheAttachedSymbolFlag_CarriesItsRawValue()
    {
        var parsed = HandoffArgParser.Parse(["--symbol=BTC/USDT"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection("BTC/USDT", null));
    }

    [Fact]
    public void TheExchangeFlagCarriesAlone_AnExchangeOnlySelection()
    {
        var parsed = HandoffArgParser.Parse(["--exchange", "binance"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection(null, "binance"));
    }

    [Fact]
    public void TheAttachedExchangeFlag_CarriesItsRawValue()
    {
        var parsed = HandoffArgParser.Parse(["--exchange=kraken"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection(null, "kraken"));
    }

    [Fact]
    public void ThePairOnBothFlags_CarriesTheFullSelection()
    {
        var parsed = HandoffArgParser.Parse(["--symbol", "BTCUSDT", "--exchange", "binance"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection("BTCUSDT", "binance"));
    }

    [Fact]
    public void TheProtocolString_DecodesIntoTheSelection()
    {
        var parsed = HandoffArgParser.Parse(["zetta://open?symbol=BTC%2FUSDT&exchange=binance"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection("BTC/USDT", "binance"));
    }

    [Fact]
    public void TheProtocolString_RawQueryValueKeepsItsSlash()
    {
        var parsed = HandoffArgParser.Parse(["zetta://open?symbol=BTC/USDT"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().Be(new HandoffSelection("BTC/USDT", null));
    }

    [Fact]
    public void TheRoutelessProtocolString_IsAValidNoOp()
    {
        var parsed = HandoffArgParser.Parse(["zetta://open"]);

        parsed.Rejection.Should().BeNull();
        parsed.Selection.Should().BeNull("the open route with no query carries nothing to apply");
    }

    #endregion Goldens

    #region Rejections

    public static TheoryData<string[]> OrderAndUnknownShapes => new()
    {
        // Every position-shaped token outside the allowlist refuses by
        // position: the order-shaped flags included, unchecked beyond it.
        new[] { "--order" },
        new[] { "--cancel", "BTC/USDT" },
        new[] { "--side", "buy" },
        new[] { "--quantity", "1" },
        new[] { "--price", "100000" },
    };

    [Theory]
    [MemberData(nameof(OrderAndUnknownShapes))]
    public void AnyTokenOutsideTheAllowlist_IsAHardRejection(string[] argv)
    {
        var parsed = HandoffArgParser.Parse(argv);

        parsed.Rejection.Should().Be(HandoffRejectionReason.UnknownToken);
        parsed.Selection.Should().BeNull();
    }

    [Fact]
    public void AFlagEndingTheVector_IsAMissingValue()
    {
        HandoffArgParser.Parse(["--symbol"]).Rejection.Should().Be(HandoffRejectionReason.MissingValue);
    }

    [Fact]
    public void AFlagTakingAnotherFlag_UnderstandsTheShapeAndRefuses()
    {
        HandoffArgParser.Parse(["--symbol", "--exchange", "binance"]).Rejection
            .Should().Be(HandoffRejectionReason.FlagShapedValue);
    }

    [Fact]
    public void AnAttachedEmptyValue_IsAnEmptyValue()
    {
        HandoffArgParser.Parse(["--symbol="]).Rejection.Should().Be(HandoffRejectionReason.EmptyValue);
    }

    [Fact]
    public void AWhitespaceValue_IsAnEmptyValue()
    {
        HandoffArgParser.Parse(["--symbol", "   "]).Rejection.Should().Be(HandoffRejectionReason.EmptyValue);
    }

    [Fact]
    public void ARepeatedSymbol_IsTheAmbiguityRefusal()
    {
        HandoffArgParser.Parse(["--symbol", "BTC/USDT", "--symbol", "ETH/USDT"]).Rejection
            .Should().Be(HandoffRejectionReason.DuplicateSelectionSource);
    }

    [Fact]
    public void ARepeatedExchange_IsTheAmbiguityRefusal()
    {
        HandoffArgParser.Parse(["--exchange", "binance", "--exchange=ib"]).Rejection
            .Should().Be(HandoffRejectionReason.DuplicateSelectionSource);
    }

    [Fact]
    public void AForeignRoute_IsAnUnrecognizedProtocolRoute()
    {
        HandoffArgParser.Parse(["zetta://trade?symbol=BTC/USDT"]).Rejection
            .Should().Be(HandoffRejectionReason.UnrecognizedProtocolRoute);
    }

    [Fact]
    public void AForeignQueryKey_IsAnUnknownProtocolQueryKey()
    {
        HandoffArgParser.Parse(["zetta://open?bogus=1"]).Rejection
            .Should().Be(HandoffRejectionReason.UnknownProtocolQueryKey);
    }

    [Fact]
    public void AnEmptyProtocolSymbolValue_IsAnEmptyValue()
    {
        HandoffArgParser.Parse(["zetta://open?symbol="]).Rejection
            .Should().Be(HandoffRejectionReason.EmptyValue);
    }

    [Fact]
    public void AProtocolStringWithBlanksInTheHost_IsAMalformedUri()
    {
        HandoffArgParser.Parse(["zetta://op en?x=1"]).Rejection
            .Should().Be(HandoffRejectionReason.MalformedProtocol);
    }

    [Fact]
    public void AFlagSelectionAndAProtocolSelection_RefuseTheCollision()
    {
        HandoffArgParser.Parse(["--symbol", "BTC/USDT", "zetta://open?symbol=ETH/USDT"]).Rejection
            .Should().Be(HandoffRejectionReason.DuplicateSelectionSource);
    }

    [Fact]
    public void TwoProtocolSelections_RefuseTheCollision()
    {
        HandoffArgParser.Parse(
        [
            "zetta://open?symbol=BTC/USDT",
            "zetta://open?symbol=ETH/USDT",
        ]).Rejection.Should().Be(HandoffRejectionReason.DuplicateSelectionSource);
    }

    #endregion Rejections
}
