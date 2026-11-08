namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Strategies;

/// <summary>
/// The strategy-seam hosting leg (T-111 composition + T-112 surface): one
/// service feeds the chart's delivered series loads into
/// <see cref="StrategyHost"/>, runs the candle-close evaluation pump for
/// the application lifetime, and broadcasts every stamped
/// <see cref="StrategySignal"/> to the UI layer — the chart's marker
/// overlay and the signal rail. A signal is an INERT event: this service
/// owns no trading dependency at all (no <c>IRiskManager</c>, no
/// <c>ITradingAdapter</c>, no <c>IConfirmationService</c>); a consumer may
/// prefill an order ticket, and the human decides.
/// </summary>
/// <remarks>
/// <para>
/// The upstream feed is the chart's full series loads: every delivered
/// bar that is provably CLOSED and advances the series becomes a
/// <see cref="ClosedCandle"/> — a bar whose cadence still runs
/// (<see cref="TimeFrameCalendar"/>'s next open lies in the future) is the
/// in-flight candle and is never judged; a bar that does not advance the
/// last-fed open is a re-covered delivery (a flip back to a loaded
/// series) and is dropped, so a candle enters the host exactly once per
/// series. Older-history prepends and repairs ride their own chart
/// streams and are deliberately NOT fed: a bar the pump never evaluated
/// produces no signal, and that absence is the honest surface (no
/// silently invented markers).
/// </para>
/// <para>
/// Stances are kept per series for the overlay lookups and pruned to the
/// series' served window on every load, so the memory bound tracks the
/// displayed bars. The pump is a supervised hosted task: a fault is
/// logged loudly, never a default substituted — the standing rule.
/// </para>
/// </remarks>
public sealed class StrategySignalService : IAsyncInitializable
{
    #region Private Fields

    private readonly IObservable<ChartSeriesPage> _seriesPages;
    private readonly IStrategyCatalog _catalog;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The service's own logger (per-type, created off the factory).</summary>
    private readonly ILogger<StrategySignalService> _logger;

    /// <summary>The host pump's per-type logger (its own category, same factory).</summary>
    private readonly ILogger<StrategyHost> _hostLogger;

    /// <summary>One stance per candle open (ticks), keyed by (symbol, interval) series.</summary>
    private readonly ConcurrentDictionary<SeriesKey, ConcurrentDictionary<long, StrategyStance>> _stances = new();

    /// <summary>The last-fed open per series: the ascending-order guard.</summary>
    private readonly ConcurrentDictionary<SeriesKey, long> _lastFed = new();

    /// <summary>The raw broadcast the UI consumers subscribe.</summary>
    private readonly Subject<StrategySignal> _signals = new();

    /// <summary>The stop switch of the hosted pump; the application lifetime fires it last.</summary>
    private readonly CancellationTokenSource _pumpTokens = new();

    private StrategyHost? _host;
    private Task _pumpTask = Task.CompletedTask;
    private IDisposable? _pageSubscription;

    #endregion

    #region Constructors

    /// <summary>
    /// Create the service. The series source arrives as an observable (not
    /// as the chart instance itself) so the composition keeps a one-way
    /// dependency: the chart consumes this service for the markers; this
    /// service never touches the chart.
    /// </summary>
    /// <param name="seriesPages">The chart's delivered full series loads.</param>
    /// <param name="catalog">The discovered strategy plugins (the T-111 seam).</param>
    /// <param name="loggerFactory">The logger factory: the service and the
    /// host pump log under their own per-type categories off one factory.</param>
    /// <param name="clock">The closed-cadence delivery clock; UtcNow by default, tests pin their own.</param>
    public StrategySignalService(
        IObservable<ChartSeriesPage> seriesPages,
        IStrategyCatalog catalog,
        ILoggerFactory loggerFactory,
        Func<DateTimeOffset>? clock = null)
    {
        _seriesPages = seriesPages ?? throw new ArgumentNullException(nameof(seriesPages));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<StrategySignalService>();
        _hostLogger = loggerFactory.CreateLogger<StrategyHost>();
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    #endregion

    #region Surfaces

    /// <summary>
    /// Every stamped signal the host emitted, addressed to the strategy,
    /// the series and the stamping candle. Raw — not UI-marshaled;
    /// consumers marshal themselves (the chart and the rail do). The
    /// stream is inert output: no consumer may route it into an order path.
    /// </summary>
    public IObservable<StrategySignal> Signals => _signals;

    /// <summary>
    /// A COPY of the latest stances the host registered per candle open
    /// for one series — the payload the marker publish maps over the
    /// loaded bars. A series with no fed stances reads empty: an absence,
    /// never a fabricated stance.
    /// </summary>
    /// <param name="symbol">The series' symbol.</param>
    /// <param name="timeFrame">The series' interval.</param>
    public IReadOnlyDictionary<DateTimeOffset, StrategyStance> StancesFor(Symbol symbol, TimeFrame timeFrame)
    {
        if (!_stances.TryGetValue(new SeriesKey(symbol, timeFrame), out var stances))
        {
            return new Dictionary<DateTimeOffset, StrategyStance>();
        }

        var copy = new Dictionary<DateTimeOffset, StrategyStance>(stances.Count);
        foreach (var cell in stances)
        {
            copy[DateTimeOffset.FromUnixTimeMilliseconds(cell.Key)] = cell.Value;
        }

        return copy;
    }

    #endregion

    #region Startup

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The catalog scanned its strategy set once; the host rides this
        // service, so the pump stops only with the application.
        _host = new StrategyHost(_catalog.All, _hostLogger);

        _pageSubscription = _seriesPages.Subscribe(
            IngestPage,
            error => _logger.LogError(error, "The strategy series feed stopped unexpectedly"));

        // The hosted pump mirrors the reconnect supervisor's shape: the
        // task is held and self-logging (a fault lands in the log, never
        // a silent abandonment).
        _pumpTask = Task.Run(() => SupervisePumpAsync(_pumpTokens.Token), CancellationToken.None);
        _logger.LogInformation(
            "Strategy host started: {Count} strategies registered from the plugins directory",
            _catalog.All.Count);

        return Task.CompletedTask;
    }

    #endregion

    #region Ingest

    /// <summary>
    /// Feed one delivered page's CLOSED candles into the host. The scan is
    /// synchronous over the unbounded writer (a write never awaits) and
    /// the cadence gate plus the last-fed gate are the only filters: each
    /// provably closed candle enters exactly once, in ascending order.
    /// Stances that no longer belong to the page's window are dropped —
    /// nothing the overlay could resolve again.
    /// </summary>
    /// <param name="page">The delivered full series load.</param>
    private void IngestPage(ChartSeriesPage page)
    {
        var host = _host;
        if (host is null)
        {
            return;
        }

        var key = new SeriesKey(page.Symbol, page.TimeFrame);
        var deliveryClock = _clock();
        var lastFed = _lastFed.TryGetValue(key, out var fed) ? fed : long.MinValue;

        var entered = 0;
        foreach (var bar in page.Bars)
        {
            var open = bar.Time.ToUnixTimeMilliseconds();
            if (open <= lastFed || !IsClosedCandle(page.TimeFrame, page.Symbol, bar.Time, deliveryClock))
            {
                continue;
            }

            if (host.CandleInput.TryWrite(new ClosedCandle(page.Symbol, page.TimeFrame, bar)))
            {
                lastFed = open;
                entered++;
            }
        }

        _lastFed[key] = lastFed;
        _logger.LogDebug(
            "Strategy host feed: {Count} of {Total} candles of {Symbol} {Interval} entered the pump as closed bars",
            entered,
            page.Bars.Count,
            page.Symbol.Normalized,
            page.TimeFrame);

        if (_stances.TryGetValue(key, out var stances) && page.Bars.Count > 0)
        {
            var floor = page.Bars[0].Time.ToUnixTimeMilliseconds();
            foreach (var open in stances.Keys.Where(open => open < floor).ToArray())
            {
                stances.TryRemove(open, out _);
            }
        }
    }

    /// <summary>
    /// A bar is provably closed when its cadence's NEXT open already runs
    /// at the delivery clock: the in-flight candle (a demo anchor candle
    /// or a live venue's running kline) is never judged. The conservative
    /// margin (a session calendar that has not stepped yet only delays
    /// the marker) never labels an open candle closed.
    /// </summary>
    /// <param name="timeFrame">The bar's interval.</param>
    /// <param name="symbol">The bar's symbol (its asset class steers the cadence).</param>
    /// <param name="open">The bar's open time.</param>
    /// <param name="deliveryClock">The moment the closure is judged against.</param>
    private static bool IsClosedCandle(TimeFrame timeFrame, Symbol symbol, DateTimeOffset open, DateTimeOffset deliveryClock) =>
        TimeFrameCalendar.NextOpenAfter(timeFrame, symbol.AssetClass, open) <= deliveryClock;

    #endregion

    #region Pump

    /// <summary>
    /// Run the host pump and the signal drain together, supervising both:
    /// completion (the input writer closed) and every fault land in the
    /// log — the standing no-silent-break rule.
    /// </summary>
    private async Task SupervisePumpAsync(CancellationToken ct)
    {
        try
        {
            var host = _host ?? throw new InvalidOperationException("The host must exist before the pump starts.");
            await Task.WhenAll(host.RunAsync(ct), DrainSignalsAsync(ct, host)).ConfigureAwait(false);
            _logger.LogInformation("Strategy host pump completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Strategy host pump stopped with the application");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy host pump stopped unexpectedly");
        }
    }

    /// <summary>
    /// Consume the host's emit stream: keep the per-series stance registry
    /// current for the overlay lookups and broadcast every signal raw.
    /// </summary>
    private async Task DrainSignalsAsync(CancellationToken ct, StrategyHost host)
    {
        await foreach (var signal in host.Signals.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var stances = _stances.GetOrAdd(
                new SeriesKey(signal.Symbol, signal.Interval),
                _ => new ConcurrentDictionary<long, StrategyStance>());
            stances[signal.Candle.Time.ToUnixTimeMilliseconds()] = signal.Stance;
            _signals.OnNext(signal);
        }
    }

    #endregion

    #region Series Key

    /// <summary>The (symbol, interval) identity the per-series stances are keyed by.</summary>
    private readonly record struct SeriesKey(Symbol Symbol, TimeFrame TimeFrame);

    #endregion
}
