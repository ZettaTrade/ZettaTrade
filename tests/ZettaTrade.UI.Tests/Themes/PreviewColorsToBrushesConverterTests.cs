namespace ZettaTrade.UI.Tests.Themes;

using System.Globalization;
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Livery.Themes;
using Xunit;

/// <summary>
/// Tests for the theme preview swatch conversion: valid hex colors produce
/// solid brushes, unparseable entries are skipped so a malformed or empty
/// previewColors list renders no swatch instead of a visual glitch.
/// </summary>
public sealed class PreviewColorsToBrushesConverterTests
{
    private static readonly PreviewColorsToBrushesConverter Converter =
        PreviewColorsToBrushesConverter.Instance;

    [Fact]
    public void Convert_WithValidHexColors_ReturnsMatchingBrushes()
    {
        string[] previewColors = ["#0a0a0f", "#ff0000", "#80ff0000"];

        var brushes = Converter
            .Convert(previewColors, typeof(object), null, CultureInfo.InvariantCulture)
            .Should()
            .BeAssignableTo<ISolidColorBrush[]>()
            .Subject;

        brushes.Should().HaveCount(3);
        brushes[0].Color.Should().Be(Color.FromRgb(0x0a, 0x0a, 0x0f));
        brushes[1].Color.Should().Be(Color.FromRgb(0xff, 0x00, 0x00));
        brushes[2].Color.A.Should().Be(0x80);
    }

    [Fact]
    public void Convert_WithUnparseableHex_SkipsOnlyTheBrokenEntries()
    {
        // "not-a-color" is data noise in a hand-edited theme file; dropping
        // it keeps the strip seamless while the valid entries still render.
        string[] previewColors = ["not-a-color", "0a0a0f", "#ff0000"];

        var brushes = Converter
            .Convert(previewColors, typeof(object), null, CultureInfo.InvariantCulture)
            .Should()
            .BeAssignableTo<ISolidColorBrush[]>()
            .Subject;

        brushes.Should().HaveCount(1);
        brushes.Single().Color.Should().Be(Color.FromRgb(0xff, 0x00, 0x00));
    }

    [Fact]
    public void Convert_WithEmptyList_RendersNoSwatch()
    {
        var result = Converter.Convert(
            Array.Empty<string>(),
            typeof(object),
            null,
            CultureInfo.InvariantCulture);

        result.Should().BeAssignableTo<ISolidColorBrush[]>().Which.Should().BeEmpty();
    }

    [Fact]
    public void Convert_WithNullValue_RendersNoSwatch()
    {
        var result = Converter.Convert(
            null,
            typeof(object),
            null,
            CultureInfo.InvariantCulture);

        result.Should().BeAssignableTo<ISolidColorBrush[]>().Which.Should().BeEmpty();
    }
}
