namespace ZettaTrade.App.Tests.ViewModels;

using System;
using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the amend order dialog view model (T-075): the dialog prefills
/// the fields from the working order, parses the edits into an
/// <see cref="Services.OrderAmendRequest"/> with the invariant culture, and
/// refuses unparsable input with a localized error line — never a
/// substituted default. A trailing stop hides its price editor, and a price
/// typed into it is ignored, exactly what the hidden row promised.
/// </summary>
public sealed class AmendOrderDialogViewModelTests
{
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();

    public AmendOrderDialogViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "[Order.Buy]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    private AmendOrderDialogViewModel CreateViewModel() =>
        new(_localization);

    private static Order LimitOrder() => new()
    {
        Id = "paper-1",
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.2m),
        Price = new Price(49_000m),
        Status = OrderStatus.Pending,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = DateTimeOffset.Parse("2026-11-06T10:00:00+00:00"),
    };

    private static Order TrailingStopOrder() => new()
    {
        Id = "paper-2",
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Sell,
        Type = OrderType.TrailingStop,
        Quantity = new Quantity(0.2m),
        Price = new Price(49_500m),
        StopPrice = new Price(51_000m),
        TrailDistance = new Price(500m),
        Status = OrderStatus.Pending,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = DateTimeOffset.Parse("2026-11-06T10:00:00+00:00"),
    };

    [Fact]
    public void Initialize_LimitOrder_PrefillsFieldsAndSummarizesTheTarget()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var working = LimitOrder();

        // Act
        viewModel.Initialize(working);

        // Assert: the fields carry the panel's current values and the
        // summary shows the target's own facts (the prefilled price uses
        // the same trimmed money format the placement dialog's prefill uses).
        viewModel.QuantityInput.Should().Be("0.2");
        viewModel.PriceInput.Should().Be("49000.00");
        viewModel.IsPriceEditable.Should().BeTrue();
        viewModel.Summary.Should().Contain("[Positions.AmendSummary:")
            .And.Contain("[Order.Buy]")
            .And.Contain("0.2")
            .And.Contain("BTC/USDT")
            .And.Contain("49000.00000000")
            .And.Contain("[Positions.StatusPending]");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Initialize_TrailingStopOrder_HidesThePriceEditor()
    {
        // Arrange: a trailing stop's displayed price is its ratcheting
        // trigger — the dialog has nothing to edit there.
        var viewModel = CreateViewModel();
        var working = TrailingStopOrder();

        // Act
        viewModel.Initialize(working);

        // Assert
        viewModel.IsPriceEditable.Should().BeFalse();
        viewModel.PriceInput.Should().BeEmpty();
        viewModel.QuantityInput.Should().Be("0.2");
    }

    [Fact]
    public void TryBuildEdits_WithParsedInputs_BuildsTheRequest()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var working = LimitOrder();
        viewModel.Initialize(working);
        viewModel.QuantityInput = "0.3";
        viewModel.PriceInput = "48500";

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert: the request points at the SAME working order the panel
        // opened the flow for (the pipeline treats it as the target handle).
        request.Should().NotBeNull();
        request!.Working.Should().BeSameAs(working);
        request.NewQuantity.Should().Be(0.3m);
        request.NewLimitPrice.Should().Be(48_500m);
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryBuildEdits_WithEmptyPrice_KeepsTheOriginalLimitPrice()
    {
        // Arrange: an empty price field means "keep the original limit
        // price" — an explicit null, not a substituted value.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LimitOrder());
        viewModel.QuantityInput = "0.3";
        viewModel.PriceInput = string.Empty;

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert
        request.Should().NotBeNull();
        request!.NewLimitPrice.Should().BeNull();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryBuildEdits_WithEmptyQuantity_ReportsRequired()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LimitOrder());
        viewModel.QuantityInput = "   ";

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert
        request.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.AmendQuantityRequired]");
    }

    [Fact]
    public void TryBuildEdits_WithUnparsableQuantity_ReportsInvalidAndEchoesTheInput()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LimitOrder());
        viewModel.QuantityInput = "0,3x";

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert: the error names the field's own invalid input — the user
        // sees precisely what was refused.
        request.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.AmendQuantityInvalid:")
            .And.Contain("0,3x");
        viewModel.HasErrorMessage.Should().BeTrue();
    }

    [Fact]
    public void TryBuildEdits_WithNonPositivePrice_ReportsInvalid()
    {
        // Arrange: a zero (or negative) limit price is unparsable as an
        // amend edit, never clamped to something tradable.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LimitOrder());
        viewModel.QuantityInput = "0.3";
        viewModel.PriceInput = "0";

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert
        request.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.AmendPriceInvalid:")
            .And.Contain("0");
    }

    [Fact]
    public void TryBuildEdits_TrailingStop_IgnoresWhateverTheHiddenPriceFieldHolds()
    {
        // Arrange: the price row is hidden for trailing stops; programmatic
        // content that row never shows must not sneak into the request —
        // and a quantity-only amend parses fine otherwise.
        var viewModel = CreateViewModel();
        var working = TrailingStopOrder();
        viewModel.Initialize(working);
        viewModel.QuantityInput = "0.1";
        viewModel.PriceInput = "12345";

        // Act
        var request = viewModel.TryBuildEdits();

        // Assert
        request.Should().NotBeNull();
        request!.Working.Should().BeSameAs(working);
        request.NewQuantity.Should().Be(0.1m);
        request.NewLimitPrice.Should().BeNull("the hidden price editor cannot carry an edit");
        viewModel.ErrorMessage.Should().BeNull();
    }

    private static string FormatArgs(object[] args) => string.Join("|", args);
}
