namespace ZettaTrade.App.ViewModels;

/// <summary>
/// The click route of the signal rail (T-112): a pressed rail row hands
/// ITSELF to its sink — the sink owns what the press means. The rail's
/// sink raises the row over the <see cref="SignalRailViewModel"/>'s own
/// prefill stream, and the main window lands it on the SAME transient
/// dialog path the order book's level presses ride: the ticket PREFILLS
/// and nothing else. No signal ever reaches an order here.
/// </summary>
public interface ISignalRailPrefillSink
{
    /// <summary>Raise one pressed row over the rail's prefill stream.</summary>
    /// <param name="row">The pressed row.</param>
    void RaisePrefill(SignalRailRow row);
}
