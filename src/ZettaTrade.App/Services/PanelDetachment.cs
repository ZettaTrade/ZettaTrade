namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// The workspace panels detachable into their own owned windows (T-103).
/// </summary>
public enum PanelDetachId
{
    /// <summary>The chart workspace panel.</summary>
    Chart = 0,

    /// <summary>The order book + depth panel.</summary>
    Book = 1,

    /// <summary>The AI assistant panel.</summary>
    Assistant = 2,
}

/// <summary>
/// The display sample a detached window's placement is stored against
/// (T-103): one monitor's working area in physical pixels plus its pixel
/// density. The PLACEMENT itself stores only fractions of this area, so a
/// DPI or resolution shift between sessions never fights the window — the
/// same fractions compose sanely against whatever area lives under the
/// stored key now.
/// </summary>
/// <param name="WorkingLeft">The working area's left edge, physical pixels.</param>
/// <param name="WorkingTop">The working area's top edge, physical pixels.</param>
/// <param name="WorkingWidth">The working area's width, physical pixels.</param>
/// <param name="WorkingHeight">The working area's height, physical pixels.</param>
/// <param name="Scale">The display's pixel density at capture time.</param>
public sealed record PanelDisplaySample(
    int WorkingLeft,
    int WorkingTop,
    int WorkingWidth,
    int WorkingHeight,
    double Scale)
{
    /// <summary>Whether this sample can carry the fraction math at all (a zero-sized area cannot).</summary>
    public bool IsUsable => WorkingWidth > 0 && WorkingHeight > 0 && double.IsFinite(Scale) && Scale > 0;

    /// <summary>Whether the absolute pixel point sits inside this sample's working area.</summary>
    public bool Contains(int absoluteLeft, int absoluteTop) =>
        absoluteLeft >= WorkingLeft &&
        absoluteTop >= WorkingTop &&
        absoluteLeft < WorkingLeft + WorkingWidth &&
        absoluteTop < WorkingTop + WorkingHeight;
}

/// <summary>
/// A detached window's stored bounds as fractions of its display's working
/// area, plus the stored window state. The fractions are what survives a
/// DPI/resolution shift: the window keeps its RELATIVE position and size
/// on the display, never a fixed pixel box.
/// </summary>
/// <param name="IsMaximized">Whether the window was maximized when stored (the fractions then hold the normal bounds).</param>
/// <param name="LeftFraction">The window's left edge, a fraction of the working-area width.</param>
/// <param name="TopFraction">The window's top edge, a fraction of the working-area height.</param>
/// <param name="WidthFraction">The window's width, a fraction of the working-area width.</param>
/// <param name="HeightFraction">The window's height, a fraction of the working-area height.</param>
public sealed record PanelWindowPlacement(
    bool IsMaximized,
    double LeftFraction,
    double TopFraction,
    double WidthFraction,
    double HeightFraction)
{
    /// <summary>The extent clamp's open edge: a window below five percent of its axis is never a deliberate placement.</summary>
    internal const double MinExtentFraction = 0.05;

    /// <summary>The extent fallback when the stored number carries nothing usable.</summary>
    internal const double DefaultExtentFraction = 0.75;

    /// <summary>A sane fraction candidate: finite, clamped into <c>[0, 1]</c>. Out-of-range garbage clamps instead of throwing — the restore compose clamps again, so a stored <c>1e999</c> collapses into <c>1</c> (or <c>-1e999</c> into <c>0</c>) rather than ever producing a nonsense window box.</summary>
    public static double ClampFraction(double fraction) =>
        double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;

    /// <summary>A sane extent candidate: finite, clamped into <c>[0.05, 1]</c> — a window below five percent of its axis is never a deliberate placement.</summary>
    public static double ClampExtent(double extent) =>
        double.IsFinite(extent) ? Math.Clamp(extent, MinExtentFraction, 1) : DefaultExtentFraction;
}

/// <summary>
/// One panel's persisted detached record (T-103): which panel it names,
/// the display the window lived on, and the window's bounds as the
/// placement fractions. An attached panel carries NO entry — the record
/// list IS the detached state.
/// </summary>
/// <param name="Panel">The workspace panel the record holds.</param>
/// <param name="Display">The display sample the placement is stored against.</param>
/// <param name="Placement">The stored window bounds as the placement fractions.</param>
public sealed record DetachedPanelEntry(
    PanelDetachId Panel,
    PanelDisplaySample Display,
    PanelWindowPlacement Placement);

/// <summary>
/// The stored JSON contract of the detached-panel record (T-103): a JSON
/// array under <c>UI.DetachedPanels</c>, camelCase keys, and a fail-safe
/// parse — stored garbage drops entry by entry (the never-crash chart
/// preference canon) and an unreadable document answers an empty record,
/// never an exception.
/// </summary>
public static class DetachedPanelsContract
{
    /// <summary>The JSON options of the stored shape: camelCase keys match the settings file's other blobs (the ChartIndicators precedent).</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialize the record list into the stored shape. No filtering here —
    /// the parser's clamps fold every stored placement into its sane shape
    /// before anything else reads it, and a snapshot of a live window is
    /// already clamped by its own capture.
    /// </summary>
    /// <param name="entries">The detached panels' record list.</param>
    /// <returns>The JSON document.</returns>
    public static string Serialize(IReadOnlyList<DetachedPanelEntry> entries) =>
        JsonSerializer.Serialize(entries, SerializerOptions);

    /// <summary>
    /// Parse a stored record: a blank document, malformed JSON, a wrong
    /// root or an unusable entry all drop; the usable entries survive
    /// (parse-or-drop, never a default substitution of a panel the user
    /// never detached).
    /// </summary>
    /// <param name="json">The stored JSON document, or null when the key is absent.</param>
    /// <returns>The usable record list (empty when nothing usable was stored).</returns>
    public static IReadOnlyList<DetachedPanelEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<DetachedPanelEntry> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<DetachedPanelEntry>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // Malformed JSON answers an empty record: the next detach leg
            // rewrites the blob from live state, nothing else restores.
            return [];
        }

        // One entry per panel (a duplicated id keeps the LAST write — the
        // same panel cannot be simultaneously detached twice; the later
        // record is the fresher one) and every unusable display drops.
        return parsed
            .Where(entry => entry.Display.IsUsable &&
                            Enum.IsDefined(typeof(PanelDetachId), entry.Panel))
            .GroupBy(entry => entry.Panel)
            .Select(group => group.Last())
            .OrderBy(entry => (int)entry.Panel)
            .ToList();
    }
}

/// <summary>
/// The bidirectional leg between a stored placement and a real window
/// box (T-103): the snapshot folds a window's live state into the
/// fraction record, the compose folds the record back into a window box
/// against a current display sample. All legs are pure and clamped: a
/// DPI/resolution shift between sessions changes nothing but the
/// multipliers, so the same fractions compose a sane window on any
/// usable display.
/// </summary>
public static class PanelWindowGeometry
{
    /// <summary>
    /// Snapshot a live window box into the fraction record. The bounds
    /// ride the display's own working area, so the record carries the
    /// RELATIVE shape only.
    /// </summary>
    /// <param name="display">The display sample the window lives on.</param>
    /// <param name="positionPixel">The window's top-left corner, physical screen pixels.</param>
    /// <param name="sizePixel">The window's box size, physical pixels.</param>
    /// <param name="isMaximized">Whether the window is currently maximized (the fractions then hold the normal bounds).</param>
    /// <returns>The fraction record, clamped into its sane shape.</returns>
    public static PanelWindowPlacement Snapshot(
        PanelDisplaySample display,
        int positionLeftPixel,
        int positionTopPixel,
        double sizeWidthPixel,
        double sizeHeightPixel,
        bool isMaximized)
    {
        if (!display.IsUsable)
        {
            return new PanelWindowPlacement(
                isMaximized,
                0,
                0,
                PanelWindowPlacement.DefaultExtentFraction,
                PanelWindowPlacement.DefaultExtentFraction);
        }

        // The position fractions ride DOUBLE math: the operands are ints,
        // and an int/int division would floor every offset below one
        // screen-width to zero (192 / 1920 = 0) — the record would then
        // pin every window to the top-left corner.
        var left = PanelWindowPlacement.ClampFraction(
            (positionLeftPixel - (double)display.WorkingLeft) / display.WorkingWidth);
        var top = PanelWindowPlacement.ClampFraction(
            (positionTopPixel - (double)display.WorkingTop) / display.WorkingHeight);
        var width = PanelWindowPlacement.ClampExtent(sizeWidthPixel / display.WorkingWidth);
        var height = PanelWindowPlacement.ClampExtent(sizeHeightPixel / display.WorkingHeight);
        return new PanelWindowPlacement(isMaximized, left, top, width, height);
    }

    /// <summary>
    /// Compose the stored fractions into a window box against a display
    /// sample: the position returns in physical screen pixels, the size in
    /// logical units (the window properties' own unit — the display's
    /// pixel density is what folds the stored pixel fractions back).
    /// </summary>
    /// <param name="display">The display to compose against (its CURRENT area and density).</param>
    /// <param name="placement">The stored fraction record.</param>
    /// <returns>The window box (position in physical pixels, size in logical units), or null when either side carries nothing usable.</returns>
    public static (int PositionLeftPixel, int PositionTopPixel, double WidthLogical, double HeightLogical)? Compose(
        PanelDisplaySample display,
        PanelWindowPlacement placement)
    {
        if (!display.IsUsable)
        {
            return null;
        }

        var leftPixel = display.WorkingLeft + (int)Math.Round(
            PanelWindowPlacement.ClampFraction(placement.LeftFraction) * display.WorkingWidth);
        var topPixel = display.WorkingTop + (int)Math.Round(
            PanelWindowPlacement.ClampFraction(placement.TopFraction) * display.WorkingHeight);
        var widthLogical = PanelWindowPlacement.ClampExtent(placement.WidthFraction)
            * display.WorkingWidth / display.Scale;
        var heightLogical = PanelWindowPlacement.ClampExtent(placement.HeightFraction)
            * display.WorkingHeight / display.Scale;
        return (leftPixel, topPixel, widthLogical, heightLogical);
    }

    /// <summary>
    /// Restore lookup: the stored entry finds its live display. An exact
    /// sample match wins (the same monitor configuration as when stored);
    /// otherwise the stored fractions name an absolute point of the STORED
    /// area and any current display whose working area contains it takes
    /// over (a monitor swapped between sessions keeps the panel); nothing
    /// matches means null — the caller centers the window.
    /// </summary>
    /// <param name="samples">The current displays.</param>
    /// <param name="entry">The stored panel record.</param>
    /// <returns>The matched display plus the composed box, or null.</returns>
    public static (PanelDisplaySample Display, int PositionLeftPixel, int PositionTopPixel, double WidthLogical, double HeightLogical)? FindRestorable(
        IReadOnlyList<PanelDisplaySample> samples,
        DetachedPanelEntry entry)
    {
        var exact = samples.FirstOrDefault(sample => sample == entry.Display && sample.IsUsable);
        if (exact is not null)
        {
            var box = Compose(exact, entry.Placement);
            return box is { } found ? (exact, found.PositionLeftPixel, found.PositionTopPixel, found.WidthLogical, found.HeightLogical) : null;
        }

        // The stored area names an absolute point; a current display that
        // contains it inherits the panel (its own fractions apply — the
        // record is RELATIVE on purpose).
        if (!entry.Display.IsUsable)
        {
            return null;
        }

        var storedLeft = entry.Display.WorkingLeft
            + (int)Math.Round(PanelWindowPlacement.ClampFraction(entry.Placement.LeftFraction) * entry.Display.WorkingWidth);
        var storedTop = entry.Display.WorkingTop
            + (int)Math.Round(PanelWindowPlacement.ClampFraction(entry.Placement.TopFraction) * entry.Display.WorkingHeight);
        var remap = samples.FirstOrDefault(sample => sample.IsUsable && sample.Contains(storedLeft, storedTop));
        if (remap is null)
        {
            return null;
        }

        var composed = Compose(remap, entry.Placement);
        if (composed is not { } done)
        {
            return null;
        }

        return (remap, done.PositionLeftPixel, done.PositionTopPixel, done.WidthLogical, done.HeightLogical);
    }
}
