namespace ZettaTrade.App.Services;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Sound;

/// <summary>
/// The price-alerts engine (T-096): the one owner of the alert roster and
/// its delayed facts. The initialization loads the persisted roster through
/// <see cref="AlertCodec"/> (a wholly unreadable payload is a logged fault —
/// never a fabricated empty roster presented as truth — and an entry that
/// cannot be honored is skipped with its count, its stored facts untouched).
/// The evaluation is purely stream-driven: one ticker subscription per
/// evaluatable symbol rides the adapter porta (the app's active-exchange
/// facade), every arriving observation is routed through a channel into a
/// serial consumer task — the engine serializes all roster work and tick
/// work behind one gate, so roster edits, fires and reloads never interleave.
/// </summary>
/// <remarks>
/// Delayed semantics without timers: a snoozed alert re-arms on the next
/// arrival after its window, a percent-move alert evaluates against the
/// oldest observation the engine still holds inside the alert's own window
/// (an honest stream-side approximation of the window's leading price; the
/// cleared history means the window is not covered yet and the alert waits).
/// A fire is persistent by design: the reached definition becomes Fired with
/// the tick's own stamp, the roster persists, the beep rides a degrade lane
/// (a failed beep is a logged warning, never a broken evaluation or a
/// crash), and a reload can never re-fire it.
/// </remarks>
public sealed class AlertEngine : IAlertEngine, IAsyncDisposable
{
    #region Constants

    /// <summary>How many observations the per-symbol move history keeps at most, even
    /// if the venue streams faster than any window could want: the time prune is
    /// the primary bound, this count is the pathological-rate backstop.</summary>
    private const int MaxTrackedObservationsPerSymbol = 4_096;

    /// <summary>How many windows the history prune respects at most — a sanity
    /// ceiling so one unreachable definition cannot grow the prune horizon.</summary>
    private const int MaxWindowMinutesAcrossRoster = 10_080;

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IMarketDataAdapter _ticks;
    private readonly IAlertSoundPlayer _sound;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertEngine> _logger;

    /// <summary>The per-run fact stream.</summary>
    private readonly Subject<AlertEvent> _events = new();

    /// <summary>The single-flight gate across all roster and tick work: edits, fires and reloads never interleave.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The inbox the ticker callbacks write into; the one consumer serializes the evaluation behind the gate.</summary>
    private readonly Channel<AlertObservation> _inbox = Channel.CreateUnbounded<AlertObservation>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>The consumer's stop switch.</summary>
    private readonly CancellationTokenSource _consumerStop = new();

    private readonly List<AlertDefinition> _definitions = [];

    /// <summary>The per-symbol ascending move history the percent-move evaluation reads (stream data only, time-pruned).</summary>
    private readonly Dictionary<string, List<AlertObservation>> _history = [];

    /// <summary>The live ticker subscriptions keyed by normalized symbol (the same spelling the definitions carry).</summary>
    private readonly Dictionary<string, IDisposable> _tickSubscriptions = [];

    private bool _consumerRunning;
    private Task _consumerTask = Task.CompletedTask;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the alerts engine over its dependencies.</summary>
    /// <param name="settings">The reactive settings service (the roster key's store).</param>
    /// <param name="ticks">The active market-data source the ticker subscriptions ride (the app's exchange facade).</param>
    /// <param name="sound">The fired-alert beep seam (the engine owns the degrade lane).</param>
    /// <param name="clock">The time provider the manager ops stamp with (create, snooze).</param>
    /// <param name="logger">The engine's logger.</param>
    public AlertEngine(
        ISettingsService settings,
        IMarketDataAdapter ticks,
        IAlertSoundPlayer sound,
        TimeProvider clock,
        ILogger<AlertEngine> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructors

    #region Events

    /// <inheritdoc/>
    public IObservable<AlertEvent> Events => _events.AsObservable();

    private void Publish(AlertEvent @event) => _events.OnNext(@event);

    #endregion Events

    #region Public Surface

    /// <inheritdoc/>
    public IReadOnlyList<AlertDefinition> Definitions => _definitions;

    /// <inheritdoc/>
    public async Task<AlertDefinition> CreateAsync(
        AlertDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var created = draft.ToCreated(_clock.GetUtcNow());
            _definitions.Add(created);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            RecalculateSubscriptions();
            Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(
        Guid id,
        AlertDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _definitions.FindIndex(definition => definition.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException($"No alert '{id}' is on the roster; the edit is refused.");
            }

            var previous = _definitions[index];
            _definitions[index] = new AlertDefinition(
                previous.Id,
                draft.SymbolName,
                draft.Kind,
                draft.Trigger,
                draft.TargetPrice,
                draft.ThresholdPercent,
                draft.WindowMinutes,
                draft.Note,
                AlertStatus.Armed,
                previous.CreatedUtc,
                SnoozedUntilUtc: null,
                previous.LastFiredUtc);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            RecalculateSubscriptions();
            Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SnoozeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _definitions.FindIndex(definition => definition.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException($"No alert '{id}' is on the roster; the snooze is refused.");
            }

            var previous = _definitions[index];
            if (previous.Status == AlertStatus.Fired)
            {
                throw new InvalidOperationException(
                    "A fired alert is terminal; the snooze is refused rather than resurrected.");
            }

            _definitions[index] = previous with
            {
                Status = AlertStatus.Snoozed,
                SnoozedUntilUtc = _clock.GetUtcNow().AddMinutes(AlertSettingsKeys.SnoozeMinutes),
            };
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _definitions.FindIndex(definition => definition.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException($"No alert '{id}' is on the roster; the delete is refused.");
            }

            _definitions.RemoveAt(index);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            RecalculateSubscriptions();
            Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion Public Surface

    #region Initialization

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = await _settings
                .GetAsync<string>(AlertSettingsKeys.Section, AlertSettingsKeys.Definitions, string.Empty)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                _logger.LogInformation("No stored alert roster; the engine starts empty");
            }
            else
            {
                var decoded = AlertCodec.Decode(payload);
                _definitions.AddRange(decoded.Valid);
                if (decoded.SkippedCount > 0)
                {
                    _logger.LogWarning(
                        "The stored alert roster skipped {Count} entry/entries that could not be honored exactly; their stored facts stay untouched",
                        decoded.SkippedCount);
                }

                _logger.LogInformation(
                    "The alert roster loaded {DefinitionCount} definition(s)",
                    _definitions.Count);
            }

            StartConsumerUnlocked();
            RecalculateSubscriptions();
        }
        finally
        {
            _gate.Release();
        }

        Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
    }

    /// <summary>Arm the one consumer task exactly once (called under the gate).</summary>
    private void StartConsumerUnlocked()
    {
        if (_consumerRunning)
        {
            return;
        }

        _consumerRunning = true;
        _consumerTask = Task.Run(
            () => ConsumeAsync(_consumerStop.Token),
            CancellationToken.None);
    }

    /// <summary>
    /// The one evaluation consumer: serial observations in, one gate-serialized
    /// application out. The loop ends when the inbox completes (dispose); a
    /// fault inside one observation is logged and swallowed (the route lives),
    /// never a dead engine behind a broken tick.
    /// </summary>
    /// <param name="stopped">The consumer's stop token.</param>
    private async Task ConsumeAsync(CancellationToken stopped)
    {
        try
        {
            await foreach (var observation in _inbox.Reader.ReadAllAsync(stopped).ConfigureAwait(false))
            {
                try
                {
                    await ApplyObservationAsync(observation, stopped).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The alert evaluation slipped on one observation; the engine carries on");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The stop is the shutdown, never a fault.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The alert consumer route ended unexpectedly");
        }
    }

    #endregion Initialization

    #region Evaluation

    /// <summary>Apply one observation behind the gate: track it, evaluate the symbol's Armed alerts, make the transitions and their persistence.</summary>
    private async Task ApplyObservationAsync(AlertObservation observation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(observation.SymbolName) || observation.Price <= 0m)
        {
            // A malformed observation evaluates nothing: the route drops it, nothing defaults.
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var evaluatable = _definitions
                .Where(definition => definition.SymbolName == observation.SymbolName && definition.IsEvaluatable)
                .ToList();

            if (evaluatable.Count == 0)
            {
                // Nothing listens on this symbol anymore: the history goes (bounds) and the tick passes.
                _history.Remove(observation.SymbolName);
                return;
            }

            if (evaluatable.Exists(definition => definition.Kind == PriceAlertKind.PercentMove))
            {
                TrackObservation(observation);
            }

            var changed = false;
            foreach (var definition in evaluatable)
            {
                if (definition.Status == AlertStatus.Snoozed)
                {
                    if (definition.SnoozedUntilUtc is not { } until || observation.OccurredUtc < until)
                    {
                        continue;
                    }

                    // The snooze window expired on an arriving tick: re-arm NOW,
                    // evaluate later (the re-arm tick itself is not an evaluation).
                    Replace(new AlertDefinition(
                        definition.Id,
                        definition.SymbolName,
                        definition.Kind,
                        definition.Trigger,
                        definition.TargetPrice,
                        definition.ThresholdPercent,
                        definition.WindowMinutes,
                        definition.Note,
                        AlertStatus.Armed,
                        definition.CreatedUtc,
                        SnoozedUntilUtc: null,
                        definition.LastFiredUtc));
                    changed = true;
                    continue;
                }

                if (!IsReached(definition, observation))
                {
                    continue;
                }

                Fire(definition, observation);
                changed = true;
            }

            if (changed)
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Does the definition's condition hold on this observation exactly?</summary>
    private bool IsReached(AlertDefinition definition, AlertObservation observation) => definition.Kind switch
    {
        PriceAlertKind.Price => AlertEdge.IsLevelHit(definition.Trigger, observation.Price, definition.TargetPrice),
        PriceAlertKind.PercentMove => IsMoveReached(definition, observation),
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Kind, "The definition names no known alert kind."),
    };

    /// <summary>
    /// The percent-move evaluation: the reference is the OLDEST observation the
    /// engine still holds inside the alert's own window; until the history
    /// covers at least one such observation the alert waits (an honest absence,
    /// never a fabricated zero-move).
    /// </summary>
    private bool IsMoveReached(AlertDefinition definition, AlertObservation observation)
    {
        if (!_history.TryGetValue(definition.SymbolName, out var series) || series.Count == 0)
        {
            return false;
        }

        var window = TimeSpan.FromMinutes(definition.WindowMinutes);
        foreach (var entry in series)
        {
            if (entry.OccurredUtc >= observation.OccurredUtc - window)
            {
                return AlertEdge.MoveMeetsThreshold(entry.Price, observation.Price, definition.ThresholdPercent);
            }
        }

        return false;
    }

    /// <summary>Keep the per-symbol history bounded and ascending: prune older than the roster's widest active window, drop the pathological tail.</summary>
    private void TrackObservation(AlertObservation observation)
    {
        if (!_history.TryGetValue(observation.SymbolName, out var series))
        {
            series = [];
            _history[observation.SymbolName] = series;
        }

        var horizon = ComputePruneHorizon(observation.SymbolName);
        var cutoff = observation.OccurredUtc - horizon;
        while (series.Count > 0 && series[0].OccurredUtc < cutoff)
        {
            series.RemoveAt(0);
        }

        // The roster's horizon is the primary bound; this count trim is the
        // pathological-rate backstop (the oldest go — the window halves early).
        if (series.Count >= MaxTrackedObservationsPerSymbol)
        {
            series.RemoveRange(0, series.Count - (MaxTrackedObservationsPerSymbol - 1));
        }

        series.Add(observation);
    }

    /// <summary>The widest active window worth keeping for the symbol: the percent alerts' largest window, sanity-capped.</summary>
    private TimeSpan ComputePruneHorizon(string symbolName)
    {
        var widest = _definitions
            .Where(definition => definition.SymbolName == symbolName &&
                                 definition.Kind == PriceAlertKind.PercentMove &&
                                 definition.IsEvaluatable)
            .Select(definition => definition.WindowMinutes)
            .DefaultIfEmpty(0)
            .Max();
        widest = Math.Min(widest, MaxWindowMinutesAcrossRoster);
        return TimeSpan.FromMinutes(widest + 1);
    }

    /// <summary>Replace the definition the id names with its reached self (Fired) and make the firing honest about it.</summary>
    private void Fire(AlertDefinition definition, AlertObservation observation)
    {
        var fired = definition with
        {
            Status = AlertStatus.Fired,
            LastFiredUtc = observation.OccurredUtc,
        };
        Replace(fired);

        _logger.LogInformation(
            "Alert '{Id}' on {Symbol} fired at {Price} (tick {OccurredUtc})",
            fired.Id,
            fired.SymbolName,
            observation.Price,
            observation.OccurredUtc);

        try
        {
            _sound.Play();
        }
        catch (Exception ex)
        {
            // The degrade contract: a failed beep is a logged warning, never a broken evaluation.
            _logger.LogWarning(ex, "The fired-alert beep failed and was degraded away");
        }

        Publish(new AlertEvent(AlertEventKind.Fired, fired, observation));
    }

    /// <summary>Swap the definition with the same id for the given one (the roster stays its identity-spine).</summary>
    private void Replace(AlertDefinition definition)
    {
        var index = _definitions.FindIndex(existing => existing.Id == definition.Id);
        if (index >= 0)
        {
            _definitions[index] = definition;
        }
    }

    /// <summary>Persist the roster through the codec's one seam (the engine is the key's sole writer).</summary>
    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settings
                .SetAsync(AlertSettingsKeys.Section, AlertSettingsKeys.Definitions, AlertCodec.Encode(_definitions))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The live engine carries on (the roster is in memory); the write
            // fault is loud but transient facts stay lost across a restart
            // only if it repeats — log at error, never swallow silently.
            _logger.LogError(ex, "Persisting the alert roster failed");
        }
    }

    #endregion Evaluation

    #region Tick Subscriptions

    /// <summary>
    /// Reconcile the ticker subscriptions with the evaluatable symbol set:
    /// a symbol with zero evaluatable alerts drops its subscription and its
    /// history, a newly armed symbol gains one. Called under the gate.
    /// </summary>
    private void RecalculateSubscriptions()
    {
        var symbols = _definitions
            .Where(definition => definition.IsEvaluatable)
            .Select(definition => definition.SymbolName)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var symbolName in _tickSubscriptions.Keys.Where(name => !symbols.Contains(name)).ToArray())
        {
            _tickSubscriptions[symbolName].Dispose();
            _tickSubscriptions.Remove(symbolName);
        }

        foreach (var symbolName in symbols.Where(name => !_tickSubscriptions.ContainsKey(name)))
        {
            Symbol symbol;
            try
            {
                symbol = Symbol.Parse(symbolName);
            }
            catch (Exception ex)
            {
                // The stored spelling refuses to parse: log loudly, skip the
                // subscription (the definition is inert until edited or the
                // spelling is corrected — nothing is silently normalized).
                _logger.LogError(ex, "The alert symbol spelling '{Symbol}' does not parse; its alerts stay inert", symbolName);
                continue;
            }

            var stream = _ticks.SubscribeTicker(symbol);
            _tickSubscriptions[symbolName] = stream.Subscribe(
                tick => _inbox.Writer.TryWrite(new AlertObservation(symbolName, tick.Timestamp, tick.LastPrice)),
                ex => _logger.LogWarning(ex, "The alert ticker stream for {Symbol} ended unexpectedly", symbolName));
        }
    }

    #endregion Tick Subscriptions

    #region Dispose

    /// <summary>Stop the route, drop the subscriptions; the in-memory roster dies with the process by design.</summary>
    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        _consumerStop.Cancel();

        try
        {
            if (_consumerRunning)
            {
                await _consumerTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The stop is the shutdown.
        }

        foreach (var subscription in _tickSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _tickSubscriptions.Clear();

        _consumerStop.Dispose();
        _gate.Dispose();
        _events.OnCompleted();
    }

    #endregion Dispose
}
