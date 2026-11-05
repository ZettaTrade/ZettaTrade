namespace Livery.Themes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

/// <summary>
/// Converts a theme's preview hex colors into solid brushes for the swatch
/// strip in the theme picker. Hex strings that do not parse are skipped
/// entirely, so a malformed or empty <c>previewColors</c> naturally renders
/// no strip instead of a visual glitch.
/// </summary>
public sealed class PreviewColorsToBrushesConverter : IValueConverter
{
    /// <summary>Shared instance for <c>x:Static</c> usage in XAML.</summary>
    public static readonly PreviewColorsToBrushesConverter Instance = new();

    private PreviewColorsToBrushesConverter()
    {
    }

    /// <inheritdoc/>
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is not IEnumerable<string> colors)
        {
            return Array.Empty<ISolidColorBrush>();
        }

        return colors
            .Select(ParseBrush)
            .Where(brush => brush is not null)
            .Cast<ISolidColorBrush>()
            .ToArray();
    }

    /// <summary>Not supported: the preview strip is read-only.</summary>
    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        BindingOperations.DoNothing;

    private static ISolidColorBrush? ParseBrush(string text)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(text));
        }
        catch (Exception)
        {
            // A malformed hex swatch is data noise, not an application
            // error: skip it so the strip stays seamless.
            return null;
        }
    }
}
