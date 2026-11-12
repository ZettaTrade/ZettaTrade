namespace ZettaTrade.App.Services;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The daily loss governor's state seam (T-189): the risk manager reads it
/// to gate new orders, the order ticket reads it for its banner and writes
/// the operator's reset through it. The implementation rides the PnL
/// engine and the settings store; the seam exists so the ticket's unit
/// battery substitutes the facts without a ledger harness.
/// </summary>
public interface IDailyLossLimit
{
    /// <summary>
    /// Read the governor's honest facts for one symbol on the current lane
    /// (see the implementation's remarks for the window and bucket
    /// semantics). An engine fault propagates: the risk manager turns it
    /// into a fail-closed rejection, the ticket's banner treats it as
    /// "unknown, stay hidden".
    /// </summary>
    Task<DailyLossState> ReadStateAsync(Core.ValueObjects.Symbol symbol, CancellationToken ct = default);

    /// <summary>
    /// The operator's manual reset: write the reset marker at NOW, lifting
    /// the block for the rest of the day.
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}
