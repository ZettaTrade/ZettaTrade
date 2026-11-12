namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

/// <summary>
/// The T-156 ticket redesign's source pins: the side reads as the segmented
/// Buy/Sell pair (the ComboBox is gone), the CTA is the primary filled
/// action tinted by the picked side, the sizer presets are chips, every
/// field's inline error binds its gated visibility and the summary line is
/// demoted to the flow lane. The state fills ride the palette keys the
/// T-152 contrast publisher scores (CandleUp, Error) with the derived
/// readable foreground, and the unit captions carry the shared format key
/// in both cultures.
/// </summary>
public sealed class TicketRedesignPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string TicketXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "NewOrderDialogWindow.axaml"));

    [Fact]
    public void TheSide_IsTheSegmentedPairNotTheComboBox()
    {
        var source = TicketXaml;

        source.Should().Contain(
            "<Button Grid.Column=\"0\"\n                    Classes=\"side buy\"",
            "the buy segment is the side control's first half");
        source.Should().Contain("Classes.selected=\"{Binding IsBuySide}\"",
            "the buy segment's selection state rides the picked side");
        source.Should().Contain("Classes.selected=\"{Binding IsSellSide}\"",
            "the sell segment's selection state rides the picked side");
        source.Should().Contain("Command=\"{Binding SelectBuySideCommand}\"");
        source.Should().Contain("Command=\"{Binding SelectSellSideCommand}\"");
        source.Should().NotContain(
            "SelectedIndex=\"{Binding SelectedSideIndex}\"",
            "the combo box is gone — the segments ride the picked-side visibility, the index stays the VM's internal state");
    }

    [Fact]
    public void TheCta_IsThePrimaryFilledActionTintedByTheSide()
    {
        var source = TicketXaml;

        source.Should().Contain(
            "<Button Classes=\"cta\"",
            "the place button is the primary action");
        source.Should().Contain("Classes.buy=\"{Binding IsBuySide}\"",
            "the CTA's tint follows the picked side");
        source.Should().Contain("Classes.sell=\"{Binding IsSellSide}\"");

        source.Should().Contain(
            "<Style Selector=\"Button.side.buy.selected\">\n            <Setter Property=\"Background\" Value=\"{DynamicResource CandleUp}\" />",
            "the buy state fill rides the CandleUp key the contrast publisher scores");
        source.Should().Contain(
            "<Style Selector=\"Button.side.sell.selected\">\n            <Setter Property=\"Background\" Value=\"{DynamicResource Error}\" />",
            "the sell state fill rides the Error key the contrast publisher scores");
        (source.Split(["{DynamicResource ChipForeground}"], StringSplitOptions.None).Length - 1)
            .Should().BeGreaterThanOrEqualTo(4,
            "every state-colored caption reads through the derived readable foreground");
    }

    [Fact]
    public void TheSizerPresets_RideTheChipActionClass()
    {
        var source = TicketXaml;

        (source.Split(["Classes=\"chip-action\""], StringSplitOptions.None).Length - 1)
            .Should().Be(3, "the three balance-share presets are the chips");
        source.Should().Contain(
            "<Setter Property=\"Padding\" Value=\"{StaticResource InsetS}\" />",
            "the chip's padding rides the spacing scale");
        source.Should().Contain(
            "<Style Selector=\"Button.chip-action\">",
            "the preset chip shape is the ticket's own style");
    }

    [Fact]
    public void TheFieldErrors_RenderInlineWithGatedVisibility()
    {
        var source = TicketXaml;

        source.Should().Contain("Classes=\"field-error\"", "the inline error style exists");
        source.Should().Contain("Text=\"{Binding SymbolError}\"");
        source.Should().Contain("Text=\"{Binding QuantityError}\"");
        source.Should().Contain("Text=\"{Binding PriceError}\"");
        source.Should().Contain("Text=\"{Binding TriggerError}\"");
        source.Should().Contain("Text=\"{Binding TrailError}\"");
        source.Should().Contain("Text=\"{Binding OcoStopLimitError}\"");

        source.Should().Contain("IsVisible=\"{Binding IsPriceErrorVisible}\"",
            "the price error line hides with its row");
        source.Should().Contain("IsVisible=\"{Binding IsTriggerErrorVisible}\"");
        source.Should().Contain("IsVisible=\"{Binding IsTrailErrorVisible}\"");
        source.Should().Contain("IsVisible=\"{Binding IsOcoStopLimitErrorVisible}\"");

        source.Should().Contain(
            "<Style Selector=\"TextBlock.field-error\">\n            <Setter Property=\"FontSize\" Value=\"{StaticResource CaptionFontSize}\" />",
            "the inline error rides the ramp's caption step");
    }

    [Fact]
    public void TheSummary_IsDemotedToTheFlowLane()
    {
        var source = TicketXaml;

        source.Should().Contain(
            "IsVisible=\"{Binding HasSummaryError}\"",
            "the summary line shows only the flow failures");
        source.Should().NotContain(
            "IsVisible=\"{Binding HasErrorMessage}\"",
            "the raw error-message visibility is the demoted contract");
    }

    [Fact]
    public void TheUnitCaptions_RideTheSharedHintFormat()
    {
        var source = TicketXaml;

        source.Should().Contain("<Run Text=\"{Binding QuantityUnitHint, Mode=OneWay}\" />");
        (source.Split(["{Binding PriceUnitHint, Mode=OneWay}"], StringSplitOptions.None).Length - 1)
            .Should().Be(3, "the plain, the OCO and the bracket entry captions carry the same unit hint");

        foreach (var resource in new[] { "Strings.resx", "Strings.ru.resx" })
        {
            var resx = File.ReadAllText(
                Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", resource));
            resx.Should().Contain(
                "NewOrder.UnitHintFormat",
                "the shared unit-hint format ships in both cultures");
        }
    }

    [Fact]
    public void TheBracketMode_CarriesItsEntryKindItsOwnTakeProfitFieldAndItsInlineError()
    {
        var source = TicketXaml;

        source.Should().Contain(
            "<ComboBoxItem AutomationProperties.AutomationId=\"OrderTypeBracketItem\">",
            "the type combo lists the bracket mode");
        source.Should().Contain(
            "<ComboBox AutomationProperties.AutomationId=\"BracketEntryKindComboBox\"\n                  SelectedIndex=\"{Binding BracketEntryKindIndex}\"",
            "the entry kind combo rides the view model's index");
        source.Should().Contain(
            "<TextBox Text=\"{Binding BracketTakeProfitInput}\"\n                 AutomationProperties.AutomationId=\"BracketTakeProfitInput\"",
            "the take-profit leg prices from its own field");
        source.Should().Contain("Text=\"{Binding BracketTakeProfitError}\"",
            "the take-profit parse refusal renders inline");
        source.Should().Contain("IsVisible=\"{Binding IsBracketTakeProfitErrorVisible}\"",
            "the take-profit error line hides with its row");

        foreach (var resource in new[] { "Strings.resx", "Strings.ru.resx" })
        {
            var resx = File.ReadAllText(
                Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", resource));
            resx.Should().Contain(
                "Order.Bracket",
                "the bracket mode's combo caption ships in both cultures");
            resx.Should().Contain(
                "NewOrder.BracketEntryPrice",
                "the bracket limit entry's caption ships in both cultures");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZettaTrade.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run inside a ZettaTrade repository checkout");
        return directory!.FullName;
    }
}
