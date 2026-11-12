namespace ZettaTrade.Core.Tests.Entities;

using System;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Xunit;

/// <summary>
/// Tests for <see cref="OrderRequest"/> domain validation rules.
/// </summary>
public sealed class OrderRequestTests
{
    private static OrderRequest ValidOrder() => new()
    {
        Symbol = new Symbol("BTC", "USDT", AssetClass.Crypto),
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = new Quantity(1m),
    };

    [Fact]
    public void Validate_MarketOrderWithoutPriceWithPositiveQuantity_ShouldNotThrow()
    {
        // Arrange: a market order needs no price.
        var request = ValidOrder();

        // Act
        var act = request.Validate;

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_GtdOrderWithoutExpiry_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { Type = OrderType.Limit, Price = new Price(50_000m), TimeInForce = TimeInForce.GTD, Expiry = null };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Expiry*");
    }

    [Fact]
    public void Validate_ExpiredOrder_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { Type = OrderType.Limit, Price = new Price(50_000m), TimeInForce = TimeInForce.GTD, Expiry = DateTimeOffset.UtcNow.AddHours(-1) };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*future*");
    }

    [Fact]
    public void Validate_LimitOrderWithoutPrice_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { Type = OrderType.Limit, Price = null };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Price*Limit*");
    }

    [Fact]
    public void Validate_StopLimitOrderWithoutStopPrice_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with
        {
            Type = OrderType.StopLimit,
            Price = new Price(50_000m),
            StopPrice = null,
        };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*StopPrice*");
    }

    [Fact]
    public void Validate_StopMarketOrderWithStopPriceAndNoPrice_ShouldNotThrow()
    {
        // Arrange
        var request = ValidOrder() with
        {
            Type = OrderType.StopMarket,
            StopPrice = new Price(48_000m),
        };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NonPositiveQuantity_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { Quantity = new Quantity(0m) };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Quantity*positive*");
    }

    [Fact]
    public void Validate_UndefinedSide_ShouldThrow()
    {
        // Arrange: a numeric ride-through casts to an out-of-set member —
        // Validate is the last line refusing it toward execution (BUG-007).
        var request = ValidOrder() with { Side = (OrderSide)999 };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*side*999*");
    }

    [Fact]
    public void Validate_UndefinedType_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { Type = (OrderType)777 };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*type*777*");
    }

    [Fact]
    public void Validate_UndefinedTimeInForce_ShouldThrow()
    {
        // Arrange
        var request = ValidOrder() with { TimeInForce = (TimeInForce)123 };

        // Act
        var act = request.Validate;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*time-in-force*123*");
    }

    // ------------------------------------------------------------------
    // T-074 conditional shapes: the stop-limit trigger-vs-limit matrix and
    // the trailing-stop parameter requirements. An unparsable or inverted
    // parameter is a returned error, never a defaulted one.
    // ------------------------------------------------------------------

    /// <summary>A sell stop-limit with the limit inside the trigger: arming side.</summary>
    [Fact]
    public void Validate_StopLimitArmingMatrixOnBothSides_ShouldNotThrow()
    {
        // A Buy stop-limit chases upward: the limit sits at or above the trigger.
        var buy = ValidOrder() with
        {
            Type = OrderType.StopLimit,
            Price = new Price(50_100m),
            StopPrice = new Price(50_000m),
        };
        buy.Invoking(r => r.Validate()).Should().NotThrow();

        // A Sell stop-limit protects downward: the limit sits at or below the trigger.
        var sell = ValidOrder() with
        {
            Side = OrderSide.Sell,
            Type = OrderType.StopLimit,
            Price = new Price(49_900m),
            StopPrice = new Price(50_000m),
        };
        sell.Invoking(r => r.Validate()).Should().NotThrow();
    }

    /// <summary>A Buy stop-limit whose trigger sits above the limit is inverted.</summary>
    [Fact]
    public void Validate_StopLimitBuyTriggerAboveLimit_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.StopLimit,
            Price = new Price(50_000m),
            StopPrice = new Price(50_900m),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*Buy*at or above*");
    }

    /// <summary>A Sell stop-limit whose trigger sits below the limit is inverted.</summary>
    [Fact]
    public void Validate_StopLimitSellTriggerBelowLimit_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Side = OrderSide.Sell,
            Type = OrderType.StopLimit,
            Price = new Price(49_000m),
            StopPrice = new Price(48_100m),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*Sell*at or below*");
    }

    [Fact]
    public void Validate_TrailingStopWithoutTrailDistance_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.TrailingStop,
            StopPrice = new Price(49_500m),
            TrailDistance = null,
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*TrailDistance*required*TrailingStop*");
    }

    [Fact]
    public void Validate_TrailingStopWithNonPositiveTrail_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.TrailingStop,
            StopPrice = new Price(49_500m),
            TrailDistance = new Price(0m),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*positive*");
    }

    [Fact]
    public void Validate_TrailingStopWithoutStopPriceSeed_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.TrailingStop,
            TrailDistance = new Price(500m),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*StopPrice*");
    }

    [Fact]
    public void Validate_TrailingStopWithSeedAndTrail_ShouldNotThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.TrailingStop,
            StopPrice = new Price(49_500m),
            TrailDistance = new Price(500m),
            OcoGroupId = "oco-abc",
        };

        request.Invoking(r => r.Validate()).Should().NotThrow();
    }

    #region Bracket Shape (T-186)

    /// <summary>A valid bracket payload for a Buy entry: opposite-side exits
    /// (a Sell take-profit limit, a Sell stop-loss stop-limit), the entry's
    /// quantity, one shared group id.</summary>
    private static BracketLegs ValidBracket() => new()
    {
        TakeProfit = new OrderRequest
        {
            Symbol = new Symbol("BTC", "USDT", AssetClass.Crypto),
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            Quantity = new Quantity(1m),
            Price = new Price(52_000m),
            OcoGroupId = "oco-bracket",
        },
        StopLoss = new OrderRequest
        {
            Symbol = new Symbol("BTC", "USDT", AssetClass.Crypto),
            Side = OrderSide.Sell,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(1m),
            Price = new Price(48_900m),
            StopPrice = new Price(49_000m),
            OcoGroupId = "oco-bracket",
        },
    };

    [Fact]
    public void Validate_BracketMarketEntryWithOppositeExits_ShouldNotThrow()
    {
        var request = ValidOrder() with { BracketLegs = ValidBracket() };

        request.Invoking(r => r.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_BracketLimitEntryWithOppositeExits_ShouldNotThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.Limit,
            Price = new Price(50_000m),
            BracketLegs = ValidBracket(),
        };

        request.Invoking(r => r.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_BracketEntryConditionalShape_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            Type = OrderType.StopLimit,
            Price = new Price(50_000m),
            StopPrice = new Price(49_000m),
            BracketLegs = ValidBracket(),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*market or limit*");
    }

    [Fact]
    public void Validate_BracketEntryCarryingOcoGroup_ShouldThrow()
    {
        var request = ValidOrder() with
        {
            OcoGroupId = "oco-entry",
            BracketLegs = ValidBracket(),
        };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*OCO group*");
    }

    [Fact]
    public void Validate_BracketTakeProfitNotALimit_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            TakeProfit = ValidBracket().TakeProfit with { Type = OrderType.StopMarket, Price = null, StopPrice = new Price(52_000m) },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*take-profit*Limit*");
    }

    [Fact]
    public void Validate_BracketStopLossNotAStopLimit_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            StopLoss = ValidBracket().StopLoss with { Type = OrderType.Limit, StopPrice = null },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*stop-loss*StopLimit*");
    }

    [Fact]
    public void Validate_BracketExitsSameSide_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            TakeProfit = ValidBracket().TakeProfit with { Side = OrderSide.Buy },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*exits must be Sell orders*same-side exits*");
    }

    [Fact]
    public void Validate_BracketExitQuantityMismatch_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            StopLoss = ValidBracket().StopLoss with { Quantity = new Quantity(2m) },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*quantity*");
    }

    [Fact]
    public void Validate_BracketExitsGroupMismatch_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            StopLoss = ValidBracket().StopLoss with { OcoGroupId = "oco-other" },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*share one*OCO group*");
    }

    [Fact]
    public void Validate_BracketExitWithoutGroup_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            TakeProfit = ValidBracket().TakeProfit with { OcoGroupId = null },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*share one*OCO group*");
    }

    [Fact]
    public void Validate_BracketNestedLeg_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            TakeProfit = ValidBracket().TakeProfit with { BracketLegs = ValidBracket() },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot itself carry a bracket*");
    }

    [Fact]
    public void Validate_BracketExitMissingPrice_ShouldThrow()
    {
        var bracket = ValidBracket() with
        {
            TakeProfit = ValidBracket().TakeProfit with { Price = null },
        };
        var request = ValidOrder() with { BracketLegs = bracket };

        // The leg's own validation rides the entry's: a limit without its
        // price is refused with the leg's own reason.
        request.Invoking(r => r.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*Price*Limit*");
    }

    #endregion
}
