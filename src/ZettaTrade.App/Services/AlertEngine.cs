namespace ZettaTrade.App.Services;

using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Sound;
using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The price-alerts engine (T-096, T-203): the one owner of the alert roster
/// and its delayed facts. The initialization loads the persisted roster
/// through <see cref="AlertCodec"/> (a wholly unreadable payload is a logged
/// fault — never a fabricated empty roster presented as truth — and an entry
/// that cannot be honored is skipped with its count, its stored facts
/// untouched) and re-arms the stored Paused indicator alerts whose plugin the
/// current session resolves. The evaluation is purely stream-driven: one
/// ticker subscription per evaluatable symbol rides the adapter porta (the
/// app's active-exchange facade), every arriving observation is routed
/// through a channel into a serial consumer task — the engine serializes all
/// roster work and tick work behind one gate, so roster edits, fires and
/// reloads never interleave.
/// </summary>
/// <remarks>
/// Delayed semantics without timers: a snoozed alert re-arms on the next
/// arrival after its window, a percent-move alert evaluates against the
/// oldest observation the engine still holds inside the alert's own window
/// (an honest stream-side approximation of the window's leading price; the
/// cleared history means the window is not covered yet and the alert waits).
/// The indicator cross (T-203) rides the same ticker stream with no new
/// stream of its own: the engine aggregates its ticks into one-minute
/// candles (a seeded history page opens the series when the symbol first
/// goes live with an indicator alert), and every candle CLOSE is the
/// evaluation event — a close the plugin set cannot honor pauses the alert,
/// never guesses. A fire is persistent by design: the reached definition
/// becomes Fired with the event's own stamp, the roster persists, the beep
/// rides a degrade lane (a failed beep is a logged warning, never a broken
/// evaluation or a crash), and a reload can never re-fire it.
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

    /// <summary>The candle length the indicator-cross evaluation rides (T-203): the
    /// engine aggregates its OWN ticker stream's ticks into one-minute candles —
    /// the "crossing candle close" is the minute's OHLC completing, learned at
    /// the next tick after the minute turns (stream-driven, no timers, no
    /// polling loop).</summary>
    private static readonly TimeSpan CandleSpan = TimeSpan.FromMinutes(1);

    /// <summary>How many one-minute candles the per-symbol series keeps (history
    /// seed + session closes): about half a day of M1 — a period the roster's
    /// realistic oscillators and averages fit inside; a longer period warms up
    /// from the session's own closes (the honest stream absence, never a
    /// fabricated value).</summary>
    private const int MaxCandlesPerSymbol = 720;

    /// <summary>How many seeded history candles the engine asks the adapter for
    /// when the first indicator alert on a symbol arms: the indicator's
    /// computation needs warmup bars the session has not aggregated yet, so the
    /// first close evaluates against real history instead of waiting an hour of
    /// session candles. The fetch is a one-shot per symbol per session.</summary>
    private const int SeedCandleCount = 720;

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IMarketDataAdapter _ticks;
    private readonly IIndicatorCatalog _indicators;
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

    /// <summary>The per-symbol closed one-minute candle series, ascending by open time (T-203):
    /// the seeded history page plus every session close; bounded by <see cref="MaxCandlesPerSymbol"/>.</summary>
    private readonly Dictionary<string, List<Bar>> _candles = [];

    /// <summary>The per-symbol candle the current minute is still building (presence is the flag): the
    /// open is the minute's first tick price, the close rides every later tick.</summary>
    private readonly Dictionary<string, Bar> _pendingCandles = [];

    /// <summary>The resolved indicator implementations keyed by "name:period" (the plugin set is
    /// session-fixed, so one resolution per pair per session; a null is a cached miss — the
    /// honest Paused reason, not a per-close reflection storm).</summary>
    private readonly Dictionary<string, IIndicator?> _indicatorCache = [];

    /// <summary>The symbols whose M1 history seed already ran this session (one fetch per live
    /// subscription period: a dropped subscription forgets the symbol, the next arm re-seeds).</summary>
    private readonly HashSet<string> _seededSymbols = new(StringComparer.Ordinal);

    private bool _consumerRunning;
    private Task _consumerTask = Task.CompletedTask;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the alerts engine over its dependencies.</summary>
    /// <param name="settings">The reactive settings service (the roster key's store).</param>
    /// <param name="ticks">The active market-data source the ticker subscriptions ride (the app's exchange facade).</param>
    /// <param name="indicators">The loaded plugin indicator catalog (T-203): the indicator-cross
    /// evaluation builds its implementations from it, and a name it cannot resolve pauses the alert.</param>
    /// <param name="sound">The fired-alert beep seam (the engine owns the degrade lane).</param>
    /// <param name="clock">The time provider the manager ops stamp with (create, snooze).</param>
    /// <param name="logger">The engine's logger.</param>
    public AlertEngine(
        ISettingsService settings,
        IMarketDataAdapter ticks,
        IIndicatorCatalog indicators,
        IAlertSoundPlayer sound,
        TimeProvider clock,
        ILogger<AlertEngine> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        _indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
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
            await RecalculateSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
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
            _definitions[index] = previous with
            {
                SymbolName = draft.SymbolName,
                Kind = draft.Kind,
                Trigger = draft.Trigger,
                TargetPrice = draft.TargetPrice,
                ThresholdPercent = draft.ThresholdPercent,
                WindowMinutes = draft.WindowMinutes,
                Note = draft.Note,
                Status = AlertStatus.Armed,
                SnoozedUntilUtc = null,
                Indicator = draft.Indicator,
            };
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            await RecalculateSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
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
            await RecalculateSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
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

            ReArmPausedUnlocked();
            StartConsumerUnlocked();
            await RecalculateSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
    }

    /// <summary>
    /// Re-arm the stored Paused indicator-cross alerts whose indicator the
    /// CURRENT session's plugin set resolves (T-203): a pause is the honest
    /// stall of a missing plugin, not a death — a session that loads the
    /// indicator resumes the evaluation. Called under the gate, before the
    /// subscriptions recalculate (the re-armed symbols seed and subscribe
    /// through the same pass). The load never rewrites the settings key: the
    /// re-arm lives in memory until the next roster persist.
    /// </summary>
    private void ReArmPausedUnlocked()
    {
        var reArmed = 0;
        for (var index = 0; index < _definitions.Count; index++)
        {
            var definition = _definitions[index];
            if (definition.Status != AlertStatus.Paused ||
                definition.Kind != PriceAlertKind.IndicatorCross ||
                definition.Indicator is not { } spec)
            {
                continue;
            }

            if (ResolveIndicator(spec.Name, spec.Period) is null)
            {
                continue;
            }

            _definitions[index] = definition with { Status = AlertStatus.Armed };
            reArmed++;
        }

        if (reArmed > 0)
        {
            _logger.LogInformation(
                "{Count} paused indicator alert(s) re-armed: the current session's plugin set resolves their indicators",
                reArmed);
        }
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

            // The candle tracking rides EVERY tick of every evaluatable symbol
            // (T-203): the in-progress minute grows, a turned minute closes the
            // previous candle and hands it to the close evaluation.
            var closedCandle = TrackCandle(observation);

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
                    Replace(definition with
                    {
                        Status = AlertStatus.Armed,
                        SnoozedUntilUtc = null,
                    });
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

            if (closedCandle is { } candle)
            {
                // The minute's candle closed: land it in the series (the
                // displayed-bar-wins tail rule) and run the close evaluation —
                // the indicator-cross alerts' own event; the raw tick above
                // never reaches them.
                AppendClosedCandle(observation.SymbolName, candle);
                if (EvaluateClosedCandle(observation.SymbolName, candle))
                {
                    changed = true;
                }
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

        // T-203: the indicator cross never judges the raw tick — its evaluation
        // event is the candle close, handled after the per-tick loop.
        PriceAlertKind.IndicatorCross => false,
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

    #region Candles

    /// <summary>
    /// Grow the symbol's candle state by one arriving tick (T-203): the tick
    /// rides the pending candle of its open-minute, a turned minute closes the
    /// pending one and starts the next. Returns the candle that just closed
    /// (the close evaluation's own event) or null when nothing closed. The
    /// OHLC is honest aggregation of the engine's own ticker stream; the
    /// volume stays zero (no trade stream is pulled — the indicator contract
    /// reads OHLC only).
    /// </summary>
    private Bar? TrackCandle(AlertObservation observation)
    {
        var minute = FloorToMinute(observation.OccurredUtc);
        if (!_pendingCandles.TryGetValue(observation.SymbolName, out var pending))
        {
            _pendingCandles[observation.SymbolName] = new Bar(
                minute,
                observation.Price,
                observation.Price,
                observation.Price,
                observation.Price,
                Volume: 0m);
            return null;
        }

        if (pending.Time == minute)
        {
            _pendingCandles[observation.SymbolName] = pending with
            {
                High = Math.Max(pending.High, observation.Price),
                Low = Math.Min(pending.Low, observation.Price),
                Close = observation.Price,
            };
            return null;
        }

        if (minute < pending.Time)
        {
            // An out-of-order stamp cannot extend a closed minute: dropped,
            // never rewritten backwards.
            return null;
        }

        _pendingCandles[observation.SymbolName] = new Bar(
            minute,
            observation.Price,
            observation.Price,
            observation.Price,
            observation.Price,
            Volume: 0m);
        return pending;
    }

    /// <summary>
    /// Land one closed candle into the symbol's series: appended ascending,
    /// EXCEPT when the seeded history's tail bar carries the same open-minute
    /// — that tail is the still-forming displayed bar the history page served,
    /// and the session's own close for the minute wins. The series stays
    /// bounded by <see cref="MaxCandlesPerSymbol"/> (the oldest go).
    /// </summary>
    private void AppendClosedCandle(string symbolName, Bar closed)
    {
        if (!_candles.TryGetValue(symbolName, out var series))
        {
            series = [];
            _candles[symbolName] = series;
        }

        if (series.Count > 0 && series[^1].Time == closed.Time)
        {
            series[^1] = closed;
            return;
        }

        series.Add(closed);
        if (series.Count > MaxCandlesPerSymbol)
        {
            series.RemoveAt(0);
        }
    }

    /// <summary>
    /// Evaluate one closed candle against the symbol's Armed indicator-cross
    /// alerts (T-203): the indicator resolves through the session-fixed cache
    /// (a miss PAUSES the alert — the honest stall, never a fabricated value),
    /// the series maps onto the plugin contract's OHLC bars, and the cross is
    /// the pure <see cref="AlertEdge"/> predicate over the last two values.
    /// Returns whether the roster changed (a pause or a fire) so the caller
    /// persists.
    /// </summary>
    private bool EvaluateClosedCandle(string symbolName, Bar closed)
    {
        if (!_candles.TryGetValue(symbolName, out var series) || series.Count < 2)
        {
            // Fewer than two closed bars: no previous close to cross from.
            return false;
        }

        var definitions = _definitions
            .Where(definition => definition.SymbolName == symbolName &&
                                 definition.Kind == PriceAlertKind.IndicatorCross &&
                                 definition.Status == AlertStatus.Armed)
            .ToList();
        if (definitions.Count == 0)
        {
            return false;
        }

        var bars = series.Select(ToIndicatorBar).ToArray();
        var changed = false;
        foreach (var definition in definitions)
        {
            var spec = definition.Indicator;
            if (spec is null)
            {
                // A decode-honored definition always carries its spec; a
                // missing one is unresolvable by construction — the same
                // honest stall as a missing plugin.
                Pause(definition);
                changed = true;
                continue;
            }

            var indicator = ResolveIndicator(spec.Name, spec.Period);
            if (indicator is null)
            {
                Pause(definition);
                changed = true;
                continue;
            }

            var values = indicator.Apply(bars);
            if (values.Count != bars.Length || values.Count < 2)
            {
                // The plugin broke its alignment contract: the close is
                // skipped loudly, no value is guessed.
                _logger.LogWarning(
                    "The indicator '{Name}' returned {ValueCount} value(s) for {BarCount} bar(s); the candle close is skipped",
                    spec.Name,
                    values.Count,
                    bars.Length);
                continue;
            }

            var hit = spec.Source switch
            {
                AlertCrossSource.IndicatorValue =>
                    AlertEdge.IsLevelCrossHit(definition.Trigger, values[^2], values[^1], spec.Level),
                AlertCrossSource.Price =>
                    AlertEdge.IsIndicatorCrossHit(
                        definition.Trigger,
                        series[^2].Close,
                        values[^2],
                        series[^1].Close,
                        values[^1]),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    spec.Source,
                    "The definition names no known cross source."),
            };
            if (!hit)
            {
                continue;
            }

            // The fire stamps the CLOSE: the bar's open minute plus the span —
            // the moment the minute's close completed.
            Fire(definition, new AlertObservation(symbolName, closed.Time.Add(CandleSpan), closed.Close));
            changed = true;
        }

        return changed;
    }

    /// <summary>Swap the definition the id names for its Paused self and publish the roster change; the persistence rides the caller's changed flag.</summary>
    private void Pause(AlertDefinition definition)
    {
        Replace(definition with { Status = AlertStatus.Paused });
        var spec = definition.Indicator;
        _logger.LogWarning(
            "Alert '{Id}' on {Symbol} paused: the indicator '{Name}' (period {Period}) is not loaded",
            definition.Id,
            definition.SymbolName,
            spec?.Name ?? "?",
            spec?.Period ?? 0);
        Publish(new AlertEvent(AlertEventKind.RosterChanged, null, null));
    }

    /// <summary>
    /// Resolve the indicator implementation for one "name:period" pair through
    /// the session-fixed cache: the plugin set never changes mid-session, so
    /// one resolution per pair is final — a cached null is the honest Paused
    /// reason, not a per-close reflection storm.
    /// </summary>
    private IIndicator? ResolveIndicator(string name, int period)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{name}:{period}");
        if (_indicatorCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IIndicator? created = null;
        try
        {
            created = _indicators.Create(name, period);
        }
        catch (Exception ex)
        {
            // The catalog owns its rejection contract (null); a thrown fault
            // degrades to the same honest miss, logged once per pair.
            _logger.LogWarning(ex, "Resolving the indicator '{Name}' (period {Period}) threw; the alert stalls", name, period);
        }

        _indicatorCache[key] = created;
        return created;
    }

    /// <summary>One engine-synthesized M1 minute's floor: the UTC instant truncated to its minute.</summary>
    private static DateTimeOffset FloorToMinute(DateTimeOffset moment)
    {
        var utc = moment.UtcDateTime;
        return new DateTimeOffset(
            new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc),
            TimeSpan.Zero);
    }

    /// <summary>Map the engine's OHLCV bar onto the plugin contract's OHLC shape (the volume the contract never reads).</summary>
    private static IndicatorBar ToIndicatorBar(Bar bar) => new(bar.Open, bar.High, bar.Low, bar.Close);

    #endregion Candles

    #region Tick Subscriptions

    /// <summary>
    /// Reconcile the ticker subscriptions with the evaluatable symbol set:
    /// a symbol with zero evaluatable alerts drops its subscription (and its
    /// candle state and seeded flag — a fresh arm re-seeds), a newly armed
    /// symbol gains one; a live symbol carrying an evaluatable indicator-cross
    /// alert gets its ONE-SHOT M1 history seed (T-203) the first time it is
    /// live this session, so the first close evaluates against real history
    /// instead of an hour of warmup. Called under the gate.
    /// </summary>
    private async Task RecalculateSubscriptionsAsync(CancellationToken cancellationToken)
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
            _pendingCandles.Remove(symbolName);
            _candles.Remove(symbolName);
            _seededSymbols.Remove(symbolName);
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

        await SeedCandleHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-shot M1 history seed (T-203): for every live, not-yet-seeded
    /// symbol with an evaluatable indicator-cross alert, pull the trailing
    /// <see cref="SeedCandleCount"/> one-minute bars once and land them as the
    /// series' opening page (only when the session has not aggregated closes
    /// yet — live data is never overwritten by history). A fetch fault is a
    /// logged warning and an empty seed: the indicator warms up from the
    /// session's own closes, the honest stream absence. The seeded flag marks
    /// the attempt done regardless of the outcome (one shot per live period).
    /// </summary>
    private async Task SeedCandleHistoryAsync(CancellationToken cancellationToken)
    {
        var seedSymbols = _definitions
            .Where(definition => definition.IsEvaluatable &&
                                 definition.Kind == PriceAlertKind.IndicatorCross &&
                                 _tickSubscriptions.ContainsKey(definition.SymbolName) &&
                                 !_seededSymbols.Contains(definition.SymbolName))
            .Select(definition => definition.SymbolName)
            .Distinct(StringComparer.Ordinal);
        foreach (var symbolName in seedSymbols)
        {
            _seededSymbols.Add(symbolName);
            try
            {
                if (_candles.TryGetValue(symbolName, out var existing) && existing.Count > 0)
                {
                    // The session already aggregates closes: live data wins,
                    // the history page would only double-count minutes.
                    continue;
                }

                var symbol = Symbol.Parse(symbolName);
                var now = _clock.GetUtcNow();
                var bars = await _ticks
                    .GetBarsAsync(symbol, TimeFrame.M1, now.AddMinutes(-SeedCandleCount), now, SeedCandleCount, cancellationToken)
                    .ConfigureAwait(false);
                _candles[symbolName] = bars
                    .Select(bar => new Bar(FloorToMinute(bar.Time), bar.Open, bar.High, bar.Low, bar.Close, bar.Volume))
                    .OrderBy(bar => bar.Time)
                    .ToList();
                _logger.LogInformation(
                    "Seeded {Count} one-minute candle(s) of history for {Symbol}'s indicator alerts",
                    _candles[symbolName].Count,
                    symbolName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Seeding the one-minute history for {Symbol} failed; its indicator alerts warm up from the session's own closes",
                    symbolName);
            }
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
