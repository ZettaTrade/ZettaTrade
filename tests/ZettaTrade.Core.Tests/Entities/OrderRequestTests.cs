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
}
