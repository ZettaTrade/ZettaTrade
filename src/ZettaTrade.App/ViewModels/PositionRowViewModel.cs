namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ReactiveUI.Primitives;

/// <summary>
/// One display row of the positions table in the positions panel. The parent
/// view model precomputes the localized display strings at load time — the
/// row itself is not a localization surface. It carries the row's action
/// surface: the close, reduce and reverse commands close over the position
/// the row rendered, and the one dynamic input is the reduce amount (parsed
/// and validated by the position actions service, never defaulted here).
/// </summary>
public sealed class PositionRowViewModel : ReactiveObject
{
    /// <summary>The partial-close amount text, parsed by the actions service against the CURRENT position — the only mutable cell of the row.</summary>
    private string _reduceQuantityInput = string.Empty;

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Localized side (Buy = long, Sell = short).</summary>
    public required string Side { get; init; }

    /// <summary>Position quantity, invariant culture.</summary>
    public required string Quantity { get; init; }

    /// <summary>Average entry price, invariant culture.</summary>
    public required string EntryPrice { get; init; }

    /// <summary>Formatted opening timestamp.</summary>
    public required string Opened { get; init; }

    /// <summary>
    /// Partial-close amount text, parsed with the invariant culture by the
    /// position actions service against the position as it stands when the
    /// order is placed (empty, unparsable, non-positive and oversized
    /// amounts are explicit rejections there). Read at command execution,
    /// so the typed value is the one the attempt validates.
    /// </summary>
    public string ReduceQuantityInput
    {
        get => _reduceQuantityInput;
        set => this.RaiseAndSetIfChanged(ref _reduceQuantityInput, value);
    }

    /// <summary>
    /// Per-row close command, created and wired by the parent view model on
    /// the shared in-flight gate; it closes over the position the row
    /// rendered and sizes the exit against the adapter's fresh state.
    /// </summary>
    public required ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; init; }

    /// <summary>Per-row partial-close command (uses <see cref="ReduceQuantityInput"/>).</summary>
    public required ReactiveCommand<RxVoid, RxVoid> ReduceCommand { get; init; }

    /// <summary>Per-row reverse command (two risk-checked legs, one confirmation).</summary>
    public required ReactiveCommand<RxVoid, RxVoid> ReverseCommand { get; init; }
}
