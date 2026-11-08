namespace ZettaTrade.App.Services;

using Livery.Contracts;

/// <summary>
/// The price-alerts manager/evaluator seam (T-096): the roster's lifecycle
/// owner (create, edit re-arm, snooze, delete), the tick-driven evaluator
/// and the persistence owner of the <see cref="AlertSettingsKeys.Definitions"/>
/// settings key. The engine is the roster's SOLE writer — no other component
/// edits that key, so an external (file-level) edit of the payload is outside
/// the contract and is neither watched nor replayed; the app owns the file
/// through this service alone.
/// </summary>
public interface IAlertEngine : IAsyncInitializable
{
    /// <summary>The live definitions, in roster order (the manager renders this snapshot).</summary>
    IReadOnlyList<AlertDefinition> Definitions { get; }

    /// <summary>The engine's fact stream (roster changes, fires) — UI consumers marshal it through ObserveOnUiThread themselves.</summary>
    IObservable<AlertEvent> Events { get; }

    /// <summary>
    /// Validate the draft and append the armed definition it names. An invalid
    /// draft is an error (nothing is silently coerced); a valid one becomes a
    /// live alert on its symbol's ticker stream immediately.
    /// </summary>
    Task<AlertDefinition> CreateAsync(
        AlertDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-arm the definition the id names with the draft's fresh facts (the
    /// edit is a re-arm: the identity and the created stamp survive, the
    /// snooze clears, the alert evaluates its condition again from now). An
    /// unknown id is an error, never a quiet no-op.
    /// </summary>
    Task UpdateAsync(
        Guid id,
        AlertDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Push the snooze window forward for the definition the id names: the
    /// alert skips evaluation until the window expires and re-arms itself on
    /// the next arriving tick (no wake timers — the engine stays purely
    /// stream-driven). A Fired alert is terminal and refuses the snooze.
    /// </summary>
    Task SnoozeAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the definition entirely (the terminal record goes with it —
    /// deletion is the user's explicit forgetting, so a reloaded session
    /// cannot resurrect the alert). An unknown id is an error.
    /// </summary>
    Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
