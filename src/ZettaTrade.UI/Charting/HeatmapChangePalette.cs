namespace ZettaTrade.UI.Charting;

using System;
using SkiaSharp;

/// <summary>
/// Maps a tile's 24h change percent to its fill color (T-121): a null or a
/// zero change keeps the neutral fill, the change grows toward the bullish
/// (up) or bearish (down) endpoint color along the channel-wise linear
/// interpolation until the change saturates at <see cref="FullSaturationPercent"/>
/// - one percent here reads as much as five: the palette never invents a
/// stronger swing than the number carries, and it never flips a sign.
/// </summary>
public static class HeatmapChangePalette
{
    /// <summary>The |change percent| that maps to the pure endpoint color.</summary>
    public const decimal FullSaturationPercent = 5m;

    /// <summary>
    /// Resolve the tile fill. A null change (the pair has not ticked) and a
    /// zero both land on the neutral color; a positive change blends the
    /// neutral toward the up color, a negative one toward the down color,
    /// each saturating at <see cref="FullSaturationPercent"/> of magnitude.
    /// </summary>
    /// <param name="changePercent">The tile's 24h change percent, or null while unpriced.</param>
    /// <param name="neutral">The resting fill (unpriced tiles, theme supplied).</param>
    /// <param name="up">The bullish endpoint (theme supplied).</param>
    /// <param name="down">The bearish endpoint (theme supplied).</param>
    /// <returns>The channel-blended fill for the tile.</returns>
    public static SKColor ColorFor(
        decimal? changePercent,
        SKColor neutral,
        SKColor up,
        SKColor down)
    {
        if (changePercent is not { } change || change == 0m)
        {
            return neutral;
        }

        var magnitude = change > 0m ? change : -change;
        var t = magnitude >= FullSaturationPercent
            ? 1d
            : (double)(magnitude / FullSaturationPercent);
        var endpoint = change > 0m ? up : down;
        return Blend(neutral, endpoint, t);
    }

    /// <summary>Channel-wise linear interpolation between two colors.</summary>
    private static SKColor Blend(SKColor from, SKColor to, double t)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t), MidpointRounding.AwayFromZero);

        return new SKColor(
            Channel(from.Red, to.Red),
            Channel(from.Green, to.Green),
            Channel(from.Blue, to.Blue),
            Channel(from.Alpha, to.Alpha));
    }
}
