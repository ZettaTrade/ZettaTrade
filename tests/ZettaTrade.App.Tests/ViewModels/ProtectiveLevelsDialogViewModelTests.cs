namespace ZettaTrade.App.Tests.ViewModels;

using System;
using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the protective-levels dialog view model (T-187): the prefill
/// seeds the fields with the levels the position carries (an absent level
/// prefills empty — the honest absent state, never a fabricated number),
/// the parse turns the invariant-culture text into
/// <see cref="ProtectiveLevelsEdits"/> (an empty field stays an explicit
/// null — the cleared level), and every refused input — unparsable,
/// non-positive, or on the wrong side of the market — reports a localized
/// error line and builds nothing, never a defaulted value. An undefined
/// side never opens the dialog at all.
/// </summary>
public sealed class ProtectiveLevelsDialogViewModelTests
{
    private const decimal MarketPrice = 50_000m;

    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();

    public ProtectiveLevelsDialogViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Order.Buy]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    private ProtectiveLevelsDialogViewModel CreateViewModel() => new(_localization);

    #region Initialize

    [Fact]
    public void Initialize_ProtectedLong_PrefillsBothLevelsAndSummarizes()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.Initialize(LongPosition(stopLoss: 48_500.25m, takeProfit: 51_000m));

        // Assert: the fields carry the position's levels in the trimmed
        // money-like prefill format, and the summary names the target's own
        // facts — the same load-time precompute the amend dialog applies.
        viewModel.StopLossInput.Should().Be("48500.25");
        viewModel.TakeProfitInput.Should().Be("51000.00");
        viewModel.Summary.Should().Contain("[Positions.ProtectSummary:")
            .And.Contain("[Order.Buy]")
            .And.Contain("0.01")
            .And.Contain("BTC/USDT")
            .And.Contain("50000.00000000");
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.HasErrorMessage.Should().BeFalse();
    }

    [Fact]
    public void Initialize_BarePosition_PrefillsEmptyFields()
    {
        // Arrange: an unprotected position — the absent level prefills
        // empty, never a fabricated number.
        var viewModel = CreateViewModel();

        // Act
        viewModel.Initialize(LongPosition(null, null));

        // Assert
        viewModel.StopLossInput.Should().BeEmpty();
        viewModel.TakeProfitInput.Should().BeEmpty();
        viewModel.Summary.Should().Contain("[Positions.ProtectSummary:");
    }

    [Fact]
    public void Initialize_UndefinedSide_ThrowsExplicitly()
    {
        // Arrange: a ride-through side value — the dialog cannot derive
        // direction rules for it, so it refuses to open (never a defaulted
        // direction matrix).
        var viewModel = CreateViewModel();

        // Act
        var open = () => viewModel.Initialize(LongPosition(49_000m, 51_000m, side: (OrderSide)99));

        // Assert
        open.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported position side 99*");
    }

    #endregion Initialize

    #region Parse

    [Fact]
    public void TryBuildEdits_WithParsedInputs_BuildsTheEdits()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = "49000";
        viewModel.TakeProfitInput = "51000.5";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert
        edits.Should().NotBeNull();
        edits!.StopLoss.Should().Be(49_000m);
        edits.TakeProfit.Should().Be(51_000.5m);
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryBuildEdits_WithEmptyInputs_ClearsBothLevels()
    {
        // Arrange: an empty field is the explicit cleared level — the whole
        // clear request rides the same nulls the service's clear path takes.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(49_000m, 51_000m));

        // Act: the prefilled pair emptied by hand.
        viewModel.StopLossInput = string.Empty;
        viewModel.TakeProfitInput = "   ";

        // Assert
        var edits = viewModel.TryBuildEdits();
        edits.Should().NotBeNull();
        edits!.StopLoss.Should().BeNull();
        edits.TakeProfit.Should().BeNull();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryBuildEdits_WithSurroundingWhitespace_ParsesTheTrimmedText()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = "  49500  ";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert: the trim is the same leniency the amend dialog's fields hold.
        edits.Should().NotBeNull();
        edits!.StopLoss.Should().Be(49_500m);
        edits.TakeProfit.Should().BeNull();
    }

    [Fact]
    public void TryBuildEdits_WithUnparsableStop_ReportsInvalidAndEchoesTheInput()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = "49.000x";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert: the error names the field's own invalid input.
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.ProtectStopNotANumber:")
            .And.Contain("49.000x");
        viewModel.HasErrorMessage.Should().BeTrue();
    }

    [Fact]
    public void TryBuildEdits_WithUnparsableTakeProfit_ReportsInvalid()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = "49000";
        viewModel.TakeProfitInput = "abc";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.ProtectTakeProfitNotANumber:")
            .And.Contain("abc");
    }

    [Fact]
    public void TryBuildEdits_WithNonPositiveLevels_AreRefused()
    {
        // Arrange: a zero or negative level is unparsable as a price level,
        // never clamped to something tradable.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = "0";
        viewModel.TakeProfitInput = "-5";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert: the stop field is refused first, never the take-profit
        // after it.
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain("[Positions.ProtectStopNotANumber:")
            .And.Contain("0");
    }

    #endregion Parse

    #region Direction Refusals

    [Theory]
    [MemberData(nameof(LongMisdirectedLevels))]
    public void TryBuildEdits_Long_MisdirectedLevels_AreRefusedInline(
        decimal? stopLoss,
        decimal? takeProfit,
        string expectedKey)
    {
        // Arrange: a level AT the market is as refused as one beyond it —
        // it either fires at once or never arms; both are refusals here,
        // and the service re-checks against the fresh price later.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.StopLossInput = stopLoss is { } stop ? stop.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        viewModel.TakeProfitInput = takeProfit is { } takeProfitLevel
            ? takeProfitLevel.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert: the localized inline refusal, nothing built.
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain($"[{expectedKey}:50000.00000000]");
    }

    [Theory]
    [MemberData(nameof(ShortMisdirectedLevels))]
    public void TryBuildEdits_Short_MirrorsTheRefusals(
        decimal? stopLoss,
        decimal? takeProfit,
        string expectedKey)
    {
        // Arrange: the short's stop sits above the market, the take-profit
        // below — the mirrored matrix, never a reinterpreted rule.
        var viewModel = CreateViewModel();
        viewModel.Initialize(ShortPosition(null, null));
        viewModel.StopLossInput = stopLoss is { } stop ? stop.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        viewModel.TakeProfitInput = takeProfit is { } takeProfitLevel
            ? takeProfitLevel.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Contain($"[{expectedKey}:50000.00000000]");
    }

    [Fact]
    public void TryBuildEdits_DirectionErrors_NameTheMarketPriceF8()
    {
        // Arrange: the refusal carries the exact market price the rule
        // judged against, in the F8 invariant shape.
        var viewModel = CreateViewModel();
        viewModel.Initialize(LongPosition(null, null));
        viewModel.TakeProfitInput = "49999.99";

        // Act
        var edits = viewModel.TryBuildEdits();

        // Assert
        edits.Should().BeNull();
        viewModel.ErrorMessage.Should().Be("[Positions.ProtectTakeProfitMustBeAboveMarket:50000.00000000]");
    }

    public static TheoryData<decimal?, decimal?, string> LongMisdirectedLevels => new()
    {
        { 50_000m, null, "Positions.ProtectStopMustBeBelowMarket" },
        { 50_100m, null, "Positions.ProtectStopMustBeBelowMarket" },
        { null, 50_000m, "Positions.ProtectTakeProfitMustBeAboveMarket" },
        { null, 49_900m, "Positions.ProtectTakeProfitMustBeAboveMarket" },
    };

    public static TheoryData<decimal?, decimal?, string> ShortMisdirectedLevels => new()
    {
        { 50_000m, null, "Positions.ProtectStopMustBeAboveMarket" },
        { 49_900m, null, "Positions.ProtectStopMustBeAboveMarket" },
        { null, 50_000m, "Positions.ProtectTakeProfitMustBeBelowMarket" },
        { null, 50_100m, "Positions.ProtectTakeProfitMustBeBelowMarket" },
    };

    #endregion Direction Refusals

    #region Test Helpers

    private static Position LongPosition(
        decimal? stopLoss,
        decimal? takeProfit,
        OrderSide side = OrderSide.Buy) => Position(side, stopLoss, takeProfit);

    private static Position ShortPosition(
        decimal? stopLoss,
        decimal? takeProfit) => Position(OrderSide.Sell, stopLoss, takeProfit);

    private static Position Position(
        OrderSide side,
        decimal? stopLoss,
        decimal? takeProfit) => new()
        {
            Id = $"pos-{side}",
            ExchangeId = "paper",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = side,
            Quantity = new Quantity(0.01m),
            EntryPrice = new Price(MarketPrice),
            CurrentPrice = new Price(MarketPrice),
            OpenedAt = DateTimeOffset.UtcNow,
            StopLoss = stopLoss is { } stop ? new Price(stop) : null,
            TakeProfit = takeProfit is { } takeProfitLevel ? new Price(takeProfitLevel) : null,
        };

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion Test Helpers
}
