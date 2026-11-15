namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;

/// <summary>
/// One symbol's trend verdict per trend time frame (T-239): the four
/// states the classifier returned (or the honest warming state a series
/// too short to judge degrades to). The feed ships raw data and the panel
/// formats the display — the same discipline as every screener row — so
/// the record carries the states themselves, never display text.
/// </summary>
/// <param name="SymbolText">The symbol's normalized text (the feed row's key, the dictionary's key).</param>
/// <param name="M15">The 15-minute frame's verdict.</param>
/// <param name="H1">The one-hour frame's verdict.</param>
/// <param name="H4">The four-hour frame's verdict.</param>
/// <param name="D1">The one-day frame's verdict.</param>
public sealed record MtfSymbolTrend(
    string SymbolText,
    MtfTrendState M15,
    MtfTrendState H1,
    MtfTrendState H4,
    MtfTrendState D1)
{
    /// <summary>The all-warming verdict: the honest cold-cache shape, never a fabricated state.</summary>
    public static MtfSymbolTrend Warming(string symbolText) => new(
        symbolText,
        MtfTrendState.Warming,
        MtfTrendState.Warming,
        MtfTrendState.Warming,
        MtfTrendState.Warming);
}

/// <summary>
/// One trend pass's whole verdict table (T-239): every screened symbol's
/// per-frame state at one computed instant. The panel holds the latest
/// snapshot and formats the trend cells from it at page time; a symbol
/// absent from the table is a warming cell, never a fabricated verdict.
/// </summary>
/// <param name="ComputedAt">The instant the pass computed the table.</param>
/// <param name="BySymbol">The verdicts keyed by the symbol's normalized text (ordinal).</param>
public sealed record MtfTrendSnapshot(
    DateTimeOffset ComputedAt,
    IReadOnlyDictionary<string, MtfSymbolTrend> BySymbol)
{
    /// <summary>
    /// The pre-warm-up shape: an empty table, every cell degrades to the
    /// warming state until the first pass publishes.
    /// </summary>
    public static MtfTrendSnapshot Empty { get; } = new(
        default,
        new Dictionary<string, MtfSymbolTrend>(StringComparer.Ordinal));
}

/// <summary>
/// The trend verdict's honest states (T-239): the three legs of the
/// classifier (the EMA slope, the ADX strength, the swing structure)
/// agree on a direction or the verdict degrades — a range with no
/// direction, or a warming state for a series the cache cannot judge yet.
/// </summary>
public enum MtfTrendState
{
    /// <summary>The cache's series is too short to judge — the honest cold state, never a fabricated verdict.</summary>
    Warming,

    /// <summary>Every leg agrees upward: the slope rises, the ADX is at the threshold, the structure prints higher highs and lows.</summary>
    Bull,

    /// <summary>Every leg agrees downward: the slope falls, the ADX is at the threshold, the structure prints lower highs and lows.</summary>
    Bear,

    /// <summary>The legs disagree or the strength is below the threshold — no direction, never an invented one.</summary>
    Range,
}
