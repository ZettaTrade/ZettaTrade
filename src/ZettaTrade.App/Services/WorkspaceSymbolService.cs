namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// App-scoped snapshot of the workspace's active symbol: the one confirmed
/// symbol the chart holds — the same <c>SymbolSelections</c> stream the chat
/// context and the order book ride — latest selection wins, null until a
/// symbol is selected. The bulk order flows scope their action to this
/// symbol at click time, so a stale per-window copy can never diverge from
/// the standing workspace choice.
/// Concurrency: one immutable reference swap per selection (the single-write
/// snapshot contract <see cref="ChatContextService"/> applies), no locks.
/// </summary>
public sealed class WorkspaceSymbolService
{
    private Symbol? _current;

    /// <summary>
    /// Create the snapshot service over the workspace symbol stream. The
    /// stream is expected to be the composition root's
    /// <c>ChartViewModel.SymbolSelections</c> (the same source the chat
    /// context subscribes to); the subscription lives for the application's
    /// lifetime, like the service itself.
    /// </summary>
    public WorkspaceSymbolService(IObservable<Symbol> symbolSelections)
    {
        ArgumentNullException.ThrowIfNull(symbolSelections);
        symbolSelections.Subscribe(ApplySymbol);
    }

    /// <summary>
    /// The symbol the workspace stands on right now; null until a symbol is
    /// selected (the bulk flows reject their action with an explicit message
    /// instead of guessing).
    /// </summary>
    public Symbol? Current => _current;

    private void ApplySymbol(Symbol symbol) => _current = symbol;
}
