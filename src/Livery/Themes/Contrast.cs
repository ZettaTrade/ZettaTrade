namespace Livery.Themes;

using System;
using System.Globalization;

/// <summary>
/// WCAG 2.x relative-luminance contrast math over the theme palettes (T-110):
/// the shorthand sRGB conversion, the luminance weighting and the standard
/// ratio — the pure functions any accessibility checker rides. Colors parse
/// as <c>#RRGGBB</c> (the theme files' only literal shape); an unparsable
/// value throws — a corrupt palette must fail loudly, never silently read
/// as conforming.
/// </summary>
public static class Contrast
{
    /// <summary>Maximum theoretical contrast: pure white over pure black.</summary>
    public const double WhiteOverBlack = 21.0;

    /// <summary>
    /// The contrast ratio of one foreground color over one background color:
    /// the lighter luminance over the darker one, plus the 0.05 haze
    /// correction on both legs, per WCAG 2.x.
    /// </summary>
    public static double Ratio(string foreground, string background)
    {
        var fg = RelativeLuminance(foreground);
        var bg = RelativeLuminance(background);

        return (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05);
    }

    /// <summary>
    /// The WCAG relative luminance of one #RRGGBB color: the shorthand
    /// sRGB decode (channels at or below the 0.04045 knee linearize at /12.92,
    /// above it through the gamma curve) weighted by the human eye's red,
    /// green and blue weights.
    /// </summary>
    public static double RelativeLuminance(string hex)
    {
        var (red, green, blue) = Parse(hex);
        var r = ChannelLuminance(red);
        var g = ChannelLuminance(green);
        var b = ChannelLuminance(blue);

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>The linearized channel value of one 0-255 sRGB byte.</summary>
    private static double ChannelLuminance(byte channel)
    {
        var value = channel / 255.0;

        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static (byte Red, byte Green, byte Blue) Parse(string hex)
    {
        if (hex is null || !hex.StartsWith('#') || hex.Length != 7)
        {
            throw new FormatException(
                $"'{hex ?? "<null>"}' is not a #RRGGBB color literal — the theme palette cannot read it.");
        }

        return (
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
