namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The ticket's pre-submit confirmation policy (T-078): decide, from the
/// reactive settings and the order's facts, whether the placement's
/// confirmation dialog is asked at all. The decision is deliberately
/// fail-ask: anything the policy cannot assess (an unknown notional) keeps
/// the gate.
/// <para>
/// The settings are re-read at every decision: a stored change (the
/// settings UI, or an external file edit riding the service's hot reload)
/// applies at the next placement — no restart. The reload path's change
/// payloads are the file model's raw elements, and this policy reads the
/// typed store through <see cref="ISettingsService.GetAsync{T}"/> instead
/// of pattern-matching change payloads, so both delivery paths land the
/// same way.
/// </para>
/// </summary>
public sealed class OrderConfirmPolicy
{
    #region Private Fields

    private readonly ISettingsService _settings;

    /// <summary>Cached live master switch, re-grounded at every decision.</summary>
    private bool _enabledLive;

    /// <summary>Cached paper master switch, re-grounded at every decision.</summary>
    private bool _enabledPaper;

    /// <summary>Cached skip threshold (floored at zero), re-grounded at every decision.</summary>
    private decimal _skipBelowNotionalUsd;

    #endregion Private Fields

    /// <summary>
    /// Create the confirmation policy over the reactive settings service.
    /// </summary>
    public OrderConfirmPolicy(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Whether the placement's confirmation dialog is asked. The risk
    /// engine's own demand (<c>RiskCheckResult.RequiresConfirmation</c>)
    /// is NOT this policy's to relax — the caller ORs the two, so a
    /// large order keeps its mandated gate whatever this answer is.
    /// </summary>
    /// <param name="isPaper">True when the order rides the paper adapter.</param>
    /// <param name="type">The final request's order type.</param>
    /// <param name="estimatedNotionalUsd">
    /// The order's estimated notional; null when no anchor prices it.
    /// </param>
    /// <param name="ct">
    /// The submit's token: the settings reads are memory-backed today, the
    /// token stays the caller's cancellation surface.
    /// </param>
    public async Task<bool> ShouldConfirmAsync(
        bool isPaper,
        OrderType type,
        decimal? estimatedNotionalUsd,
        CancellationToken ct = default)
    {
        await RefreshAsync();

        if (isPaper)
        {
            return _enabledPaper && !IsBelowSkipThreshold(estimatedNotionalUsd);
        }

        // Immediate execution: a live market order always faces its gate,
        // whatever the master switch and threshold say.
        if (type == OrderType.Market)
        {
            return true;
        }

        // Fail-ask: without a notional the threshold cannot be assessed,
        // and the gate appears rather than disappears.
        if (estimatedNotionalUsd is null)
        {
            return true;
        }

        return _enabledLive && !IsBelowSkipThreshold(estimatedNotionalUsd);
    }

    /// <summary>
    /// Re-ground the cached settings from the store: three typed reads —
    /// the store answers from memory, so the decision stays submit-paced
    /// and hot (every read sees the freshest file or UI change).
    /// </summary>
    private async Task RefreshAsync()
    {
        _enabledLive = await _settings.GetAsync<bool>(
            ConfirmSettingsKeys.Section,
            ConfirmSettingsKeys.Enabled,
            ConfirmSettingsKeys.EnabledValue);
        _enabledPaper = await _settings.GetAsync<bool>(
            ConfirmSettingsKeys.Section,
            ConfirmSettingsKeys.EnabledInPaper,
            ConfirmSettingsKeys.EnabledInPaperValue);

        var stored = await _settings.GetAsync(
            ConfirmSettingsKeys.Section,
            ConfirmSettingsKeys.SkipBelowNotionalUsd,
            ConfirmSettingsKeys.SkipBelowNotionalValue);

        // A zero or negative stored value is the no-skip floor: the
        // threshold only ever releases orders from the gate, never widens it.
        _skipBelowNotionalUsd = stored <= 0m ? 0m : stored;
    }

    /// <summary>The notional rides below the configured release threshold.</summary>
    private bool IsBelowSkipThreshold(decimal? notionalUsd) =>
        notionalUsd is { } notional && notional < _skipBelowNotionalUsd;
}
