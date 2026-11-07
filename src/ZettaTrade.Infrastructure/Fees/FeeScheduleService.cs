namespace ZettaTrade.Infrastructure.Fees;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The per-adapter fee schedule registry (T-080). Schedules come from the
/// <see cref="SettingsSection"/>/Schedules settings section — a JSON object
/// keyed by adapter id carrying maker/taker rates in bps plus a minimum fee —
/// and the shipped simulation schedule covers the paper adapter out of the
/// box (configurable by overriding its entry). An adapter without a schedule
/// is the HONEST UNKNOWN: <see cref="GetSchedule"/> returns nothing and every
/// fee surface renders the localized dash — never a zero default that hides
/// the cost.
/// <para>
/// Settings changes (the reactive canon) refresh the snapshot live: a saved
/// or hot-reloaded <see cref="SettingsSection"/> edit re-prices the next
/// estimate and from the next fill on. A malformed or invalid entry (a
/// negative rate or minimum) is dropped with a warning — never charged.
/// </para>
/// </summary>
public sealed class FeeScheduleService(
    ISettingsService settings,
    ILogger<FeeScheduleService> logger) : IAsyncInitializable
{
    #region Constants

    /// <summary>The settings section the fee schedules live under.</summary>
    public const string SettingsSection = "Fees";

    /// <summary>The settings key within the section: adapter id → schedule.</summary>
    public const string SettingsKey = "Schedules";

    /// <summary>
    /// The adapter id the shipped simulation schedule applies to: the paper
    /// adapter's identifier, so the configurability promise holds out of the
    /// box. Overriding the entry in settings replaces the shipped numbers.
    /// </summary>
    public const string PaperAdapterId = PaperTradingAdapter.ExchangeId;

    /// <summary>Shipped paper maker rate: 10 bps = 0.10% per resting fill.</summary>
    public const decimal PaperMakerRateBps = 10m;

    /// <summary>Shipped paper taker rate: 20 bps = 0.20% per crossing fill.</summary>
    public const decimal PaperTakerRateBps = 20m;

    /// <summary>Shipped paper minimum fee in the quote currency (no floor).</summary>
    public const decimal PaperMinimumFee = 0m;

    /// <summary>The shipped schedule the paper adapter's fills price with.</summary>
    private static readonly FeeSchedule ShippedPaperSchedule =
        new(PaperMakerRateBps, PaperTakerRateBps, PaperMinimumFee);

    /// <summary>The schedule map before any settings load (nothing configured).</summary>
    private static readonly IReadOnlyDictionary<string, FeeSchedule> NoSchedules =
        new Dictionary<string, FeeSchedule>(capacity: 0, comparer: StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Private Fields

    /// <summary>
    /// The current configured schedules: an immutable snapshot, swapped on
    /// every load or settings change, so readers never lock and never see a
    /// partially-updated map. Includes only VALID entries (see
    /// <see cref="Validate"/>), keyed case-insensitively by adapter id.
    /// </summary>
    private IReadOnlyDictionary<string, FeeSchedule> _schedules = NoSchedules;

    /// <summary>The settings subscription, released at disposal.</summary>
    private IDisposable? _changesSubscription;

    #endregion

    #region Public Methods

    /// <summary>Load the configured schedules and subscribe to settings changes.</summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadSchedulesAsync(cancellationToken);

        // Settings changes re-read the section: each change becomes one
        // read+swap in the reactive pipeline, strictly serialized by the
        // Switch continuation (never overlapping, never abandoned), so a
        // burst of edits converges on the latest snapshot.
        _changesSubscription = settings.Changes
            .Where(change => string.Equals(change.Section, SettingsSection, StringComparison.Ordinal))
            .Select(_ => Observable.FromAsync(
                () => LoadSchedulesAsync(CancellationToken.None)))
            .Switch()
            .Subscribe(
                _ => { },
                error => logger.LogError(error, "Fee schedules: the settings change stream failed"));

        logger.LogInformation(
            "Fee schedules ready: {ScheduleCount} adapter schedule(s) configured (+ the shipped paper default)",
            _schedules.Count);
    }

    /// <summary>The engine owns no running work: the subscription dies at disposal.</summary>
    public ValueTask DisposeAsync()
    {
        _changesSubscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The schedule configured for the given adapter: the settings entry when
    /// one exists, the shipped default for the paper adapter, otherwise
    /// nothing — the honest unknown every fee surface displays as a dash.
    /// Synchronous by contract: it reads the in-memory snapshot settings
    /// changes already refresh.
    /// </summary>
    /// <param name="exchangeId">The adapter identifier the fees would charge on.</param>
    public FeeSchedule? GetSchedule(string exchangeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeId);

        return _schedules.TryGetValue(exchangeId, out var configured)
            ? configured
            : string.Equals(exchangeId, PaperAdapterId, StringComparison.OrdinalIgnoreCase)
                ? ShippedPaperSchedule
                : null;
    }

    #endregion

    #region Private Methods

    /// <summary>Read + validate the settings section and swap the snapshot.</summary>
    private async Task LoadSchedulesAsync(CancellationToken ct)
    {
        var configured = await settings.GetAsync<IReadOnlyDictionary<string, FeeSchedule>>(
            SettingsSection,
            SettingsKey,
            defaultValue: null!);

        var valid = Validate(configured);
        _schedules = valid ?? NoSchedules;
    }

    /// <summary>
    /// Wheel the configured entries through the schedule validity rule: a
    /// negative maker/taker rate or minimum fee is a configuration error —
    /// the entry is dropped with a warning instead of being charged, a
    /// malformed payload deserializes to nothing. Returns null when every
    /// entry was invalid, so callers fall back to the shipped defaults.
    /// </summary>
    private IReadOnlyDictionary<string, FeeSchedule>? Validate(IReadOnlyDictionary<string, FeeSchedule>? configured)
    {
        if (configured is null || configured.Count == 0)
        {
            return NoSchedules;
        }

        var valid = new Dictionary<string, FeeSchedule>(configured.Count, StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        foreach (var (adapterId, schedule) in configured)
        {
            if (string.IsNullOrWhiteSpace(adapterId))
            {
                logger.LogWarning("Fee schedules: dropped a schedule entry with a blank adapter id");
                dropped++;
                continue;
            }

            if (schedule.MakerRateBps < 0m || schedule.TakerRateBps < 0m || schedule.MinimumFee < 0m)
            {
                logger.LogWarning(
                    "Fee schedules: dropped the {AdapterId} schedule entry (maker {Maker}, taker {Taker}, min {Min}): " +
                    "a negative rate or minimum fee is a configuration error",
                    adapterId,
                    schedule.MakerRateBps,
                    schedule.TakerRateBps,
                    schedule.MinimumFee);
                dropped++;
                continue;
            }

            valid[adapterId] = schedule;
        }

        return valid.Count > 0 ? valid : null;
    }

    #endregion
}
