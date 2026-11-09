namespace ZettaTrade.UI.Charting;

/// <summary>
/// The Y-axis scale modes of the price chart (T-090): the plain price
/// domain, its base-10 logarithm (long ranges read compactly), and the
/// percent change over the window's anchor close (the relative read). The
/// mode is a rendering-domain choice: the pixel pipeline stays linear and
/// works in the transformed domain — see <see cref="ChartPriceTransform"/>.
/// </summary>
public enum PriceScaleMode
{
    /// <summary>The plain price domain — the standing default.</summary>
    Linear,

    /// <summary>
    /// The base-10 logarithm of prices: equal vertical distances read as
    /// equal ratios. A non-positive price clamps visibly to the window's
    /// positive floor — the candle keeps drawing at the clamped position,
    /// the mode never silently falls back to linear.
    /// </summary>
    Logarithmic,

    /// <summary>
    /// Percent change over the window's anchor close (the first visible
    /// close): the axis reads relative movement, not absolute price. A
    /// corrupt anchor clamps to the window's positive floor the same way.
    /// </summary>
    Percent,
}
