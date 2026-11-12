namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Interfaces;

/// <summary>
/// The kill switch's venue set (T-188): the app's trading adapters, in
/// sweep order. Every venue is commanded — the panic action's job is "flat
/// everywhere", so a venue keeps no survivor behind a lane or picker
/// subtlety (the paper simulator is the standing default lane; the Bybit
/// testnet venue is the armed surface). A venue whose read faults reports
/// itself unreachable; the sweep never stops for it.
/// </summary>
public sealed class KillSwitchVenues
{
    /// <summary>Create the venue set over the trading adapters, in sweep order.</summary>
    public KillSwitchVenues(IEnumerable<ITradingAdapter> venues)
    {
        Venues = [.. venues];
    }

    /// <summary>Gets the trading adapters the sweep commands, in sweep order.</summary>
    public IReadOnlyList<ITradingAdapter> Venues { get; }
}
