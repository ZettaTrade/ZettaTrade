namespace ZettaTrade.App.Tests.ViewModels;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The opening-range minutes knob's pure parse lane (T-247): an unparsable
/// spelling answers null (the knob edits nothing — the standing value and
/// its persistence stay in charge), a readable one clamps into the legal
/// bounds. Pinned in its pure form — no VM instance, no dispatcher.
/// </summary>
public sealed class ChartOpeningRangeParseTests
{
    /// <summary>A readable spelling parses invariantly and clamps into the legal bounds.</summary>
    [Theory]
    [InlineData("30", 30)]
    [InlineData("  42  ", 42)]
    [InlineData("1", 1)]
    [InlineData("1440", 1440)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("99999999", 1440)]
    public void Parse_AReadableSpelling_ClampsIntoTheLegalBounds(string text, int expected)
    {
        ChartViewModel.ParseOpeningRangeMinutes(text).Should().Be(expected);
    }

    /// <summary>An unparsable spelling answers null: whitespace, words, a
    /// fractional form and an int-overflowing numeral all edit nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("2147483648")]
    public void Parse_AnUnreadableSpelling_EditsNothing(string? text)
    {
        ChartViewModel.ParseOpeningRangeMinutes(text).Should().BeNull();
    }
}
