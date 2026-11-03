namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Enums;

/// <summary>
/// The workspace as the assistant should see it: which exchange serves the
/// view, what symbol and time frame the trader is looking at, and the latest
/// price the ticker reported. A part that is not (yet) known is simply
/// absent — the prompt degrades honestly instead of inventing values.
/// </summary>
public sealed record ChatContext
{
    /// <summary>Display name of the active exchange.</summary>
    public string ExchangeName { get; init; } = string.Empty;

    /// <summary>The workspace symbol, once one is selected.</summary>
    public string? Symbol { get; init; }

    /// <summary>The workspace time frame (only meaningful with a symbol).</summary>
    public TimeFrame? TimeFrame { get; init; }

    /// <summary>The latest ticker price (only meaningful with a symbol).</summary>
    public decimal? LastPrice { get; init; }

    /// <summary>Whether at least a symbol is pinned (the useful baseline).</summary>
    public bool HasSymbol => Symbol is not null;
}
