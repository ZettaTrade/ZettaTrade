namespace ZettaTrade.E2E.Tests;

using FluentAssertions;
using Xunit;

/// <summary>
/// The order ticket path over the REAL process (T-176): the Trading menu
/// opens the new-order ticket, its fields render, and the cancel route
/// leaves without ever placing an order - the lane walks the ticket as a
/// user does and stops at the wire (no submit press exists in this flow).
/// </summary>
[Collection("E2E")]
public sealed class OrderTicketTests
{
    private readonly E2EAppFixture _app;

    public OrderTicketTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheTicketMenuRoute_OpensRendersAndCancels()
    {
        _app.ClickMenuItem("TradingMenu", "TradingNewOrderMenuItem");
        var dialog = _app.FindTopLevelWindowById("NewOrderDialogWindow");
        dialog.Should().NotBeNull("the menu route opens the ticket like the hotkey does");

        _app.FindDescendantById(dialog!, "QuantityInput")
            .Should().NotBeNull("the quantity field renders on the ticket");

        // The ticket opens in its default Market mode, which needs no price
        // row; the fields follow the picked type, so flipping the type to
        // Limit through the ticket's own combo is the honest user route to
        // the price field.
        var typeCombo = _app.FindDescendantById(dialog!, "OrderTypeComboBox");
        typeCombo.Should().NotBeNull("the order type row renders on the ticket");
        typeCombo!.Patterns.ExpandCollapse.Pattern.Expand();
        var limit = _app.FindElementEverywhere("OrderTypeLimitItem");
        limit.Should().NotBeNull("the limit row renders in the open type combo");
        limit!.Click();

        _app.FindDescendantById(dialog!, "PriceInput")
            .Should().NotBeNull("the price field renders once the type requires it");

        var cancel = _app.FindDescendantById(dialog!, "CancelButton");
        cancel.Should().NotBeNull("the cancel affordance renders");
        cancel!.Patterns.Invoke.Pattern.Invoke();
        _app.WaitForTopLevelWindowGone("NewOrderDialogWindow")
            .Should().BeTrue("cancel closes the ticket; nothing was ever submitted");
    }
}
