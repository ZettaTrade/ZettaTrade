namespace ZettaTrade.Infrastructure.Tests.Exchange;

using FluentAssertions;
using Infrastructure.Exchange;
using Xunit;
using ZettaTrade.Core.Enums;

/// <summary>
/// The status-mapping matrix for the Bybit trading surface (T-127): every
/// documented v5 spot-limit lifecycle string maps onto exactly the Core
/// lane it stands for, and anything the surface cannot answer for — a
/// conditional status, a casing drift, a foreign string — is an error,
/// never a defaulted value (trading-safety: an unparsable enum must not
/// flow into execution or history as a guess).
/// </summary>
public sealed class BybitOrderStatusMapperTests
{
    #region Tests: the documented set

    /// <summary>The documented v5 spot lifecycle strings and their Core lanes.</summary>
    [Theory]
    [InlineData("New", OrderStatus.Submitted)]
    [InlineData("Created", OrderStatus.Submitted)]
    [InlineData("PartiallyFilled", OrderStatus.PartiallyFilled)]
    [InlineData("Filled", OrderStatus.Filled)]
    [InlineData("Cancelled", OrderStatus.Cancelled)]
    [InlineData("PartiallyFilledCanceled", OrderStatus.Cancelled)]
    [InlineData("Deactivated", OrderStatus.Cancelled)]
    [InlineData("Rejected", OrderStatus.Rejected)]
    public void Map_DocumentedLifecycle_MapsTheCoreLane(string venueStatus, OrderStatus expected)
    {
        // Act
        var mapped = BybitOrderStatusMapper.Map(venueStatus);

        // Assert
        mapped.Should().Be(expected);
    }

    #endregion

    #region Tests: the refused set

    /// <summary>
    /// A status outside the surface's lifecycle (a conditional-order state,
    /// a casing drift, a foreign string, a blank) is refused: no lane is a
    /// fabrication.
    /// </summary>
    /// <param name="venueStatus">The venue string that must not map.</param>
    [Theory]
    [InlineData("PendingCancel")]
    [InlineData("Untriggered")]
    [InlineData("Triggered")]
    [InlineData("new")]
    [InlineData("filled")]
    [InlineData("")]
    [InlineData("TotallyUnknownVenueState")]
    public void Map_UnmappableVenueStatus_ThrowsInsteadOfDefaulting(string venueStatus)
    {
        // Act
        var map = () => BybitOrderStatusMapper.Map(venueStatus);

        // Assert: the refusal names the offending status (a blank cannot be
        // contained in the message, so the naming assert applies to non-empty).
        map.Should()
            .Throw<InvalidOperationException>();
        if (venueStatus.Length > 0)
        {
            map.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(venueStatus);
        }
    }

    #endregion
}
