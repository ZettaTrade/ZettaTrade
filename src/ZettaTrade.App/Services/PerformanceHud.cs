namespace ZettaTrade.App.Services;

using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pure gauge math of the performance HUD (T-108): every function is
/// deterministic and allocation-light, so the goldens pin the exact strings
/// and indices the overlay prints. The digits are culture-invariant on
/// purpose — a dev readout must not flip its decimal comma with the UI
/// language; only the line labels around these values localize.
/// </summary>
public static class HudMath
{
    /// <summary>Bytes in one megabyte (the memory line's unit).</summary>
    private const double BytesPerMegabyte = 1024d * 1024d;

    /// <summary>
    /// Frames per second from one drained window: the frame count the tap
    /// saw over the sampler's period. A non-positive period guards to zero
    /// (a division by zero is a bug signal, not a reading); a negative
    /// count is likewise not a reading.
    /// </summary>
    /// <param name="frames">The drained frame count.</param>
    /// <param name="elapsedSeconds">The window the count spans.</param>
    /// <returns>The frames-per-second rate.</returns>
    public static double FramesPerSecond(int frames, double elapsedSeconds)
    {
        if (frames <= 0 || elapsedSeconds <= 0)
            return 0;

        return frames / elapsedSeconds;
    }

    /// <summary>
    /// The histogram bucket a latency sample falls into: bucket <c>i</c>
    /// when <c>value</c> is at most <c>bounds[i]</c> (the bounds are
    /// inclusive upper edges, ascending), the last index
    /// (<c>bounds.Length</c>) when the value overflows every edge. A
    /// negative value (a stamp a hair ahead of the observer's now — a
    /// scheduling race between two reads of the same local clock) reads as
    /// the fastest bucket: an age below zero is still "no visible lag".
    /// </summary>
    /// <param name="valueMs">The latency sample, in milliseconds.</param>
    /// <param name="boundsMs">The ascending inclusive upper edges.</param>
    /// <returns>The bucket index, <c>0</c> … <c>boundsMs.Length</c>.</returns>
    public static int HistogramBucketIndex(double valueMs, double[] boundsMs)
    {
        if (valueMs < 0)
            return 0;

        for (var i = 0; i < boundsMs.Length; i++)
        {
            if (valueMs <= boundsMs[i])
                return i;
        }

        return boundsMs.Length;
    }

    /// <summary>
    /// The bird gauge: a fraction rendered as a bar of filled and empty
    /// segments ('█' filled, '░' empty). The fraction clamps to
    /// <c>[0, 1]</c>, so a probe glitch can never print more birds than the
    /// gauge carries; the rounding is the midpoint-away-from-zero one, so
    /// one half of a segment renders.
    /// </summary>
    /// <param name="fraction">The load fraction, clamped to <c>[0, 1]</c>.</param>
    /// <param name="segments">The gauge's segment count.</param>
    /// <returns>The gauge bar string.</returns>
    public static string GaugeBar(double fraction, int segments)
    {
        if (segments <= 0)
            return string.Empty;

        var clamped = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
        var filled = (int)Math.Round(clamped * segments, MidpointRounding.AwayFromZero);

        return new string('█', filled) + new string('░', segments - filled);
    }

    /// <summary>
    /// The working memory readout: bytes as megabytes with one decimal,
    /// culture-invariant (the localized line around it names the unit).
    /// </summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The megabyte figure, e.g. <c>"412.3"</c>.</returns>
    public static string FormatMegabytes(long bytes) =>
        (bytes / BytesPerMegabyte).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// One 1 Hz readout of the performance HUD (T-108): an immutable snapshot
/// the sampler emits and the overlay formats. The arrays are fresh copies
/// per emission — a subscriber may hold a snapshot forever without the
/// sampler's next drain overwriting it.
/// </summary>
/// <param name="ChartFps">The chart's frames-per-second over the window.</param>
/// <param name="BookUpdatesPerSecond">The book stream's updates over the window.</param>
/// <param name="WorkingSetBytes">The process's working set at the sample.</param>
/// <param name="BackpressureBacklog">The log tail's bounded channel depth at the sample.</param>
/// <param name="BackpressureCapacity">The log tail's bounded channel capacity.</param>
/// <param name="LatencyBuckets">The WS-latency histogram: bucket counts over the window (one more than the bounds — the overflow).</param>
/// <param name="LatencyBoundsMs">The histogram's ascending inclusive upper edges, in milliseconds (the static read-only shape, shared by contract).</param>
public sealed record PerformanceSnapshot(
    double ChartFps,
    int BookUpdatesPerSecond,
    long WorkingSetBytes,
    int BackpressureBacklog,
    int BackpressureCapacity,
    int[] LatencyBuckets,
    double[] LatencyBoundsMs);

/// <summary>
/// The performance HUD's sampler (T-108). OFF — the shipped state — the
/// service holds nothing: no subscription on the book stream, no timer, no
/// counters moving; the entire off cost of the feature is the chart's own
/// frame-tap branch (<c>FrameTapEnabled</c>) which never opens. START — the
/// menu's checkbox flips it — the sampler subscribes the book updates,
/// arms a 1 Hz poll (chart fps drain, working set, backpressure probe,
/// latency histogram drain) and emits a <see cref="PerformanceSnapshot"/>
/// per tick. Start and stop are the HUD ViewModel's (UI thread) calls; the
/// sampling threads only touch Interlocked counters.
/// </summary>
/// <param name="bookUpdates">The order book stream (the rate and the WS-latency samples come from its stamps).</param>
/// <param name="backpressureBacklogProbe">The backpressure gauge's depth probe (the log tail's bounded channel), or null in the tests.</param>
/// <param name="backpressureCapacity">The backpressure gauge's capacity (the probe's full mark).</param>
/// <param name="logger">The service logger.</param>
public sealed class PerformanceHudService(
    IObservable<OrderBook> bookUpdates,
    Func<int>? backpressureBacklogProbe,
    int backpressureCapacity,
    ILogger<PerformanceHudService> logger)
{
    /// <summary>The sampler's period: the HUD readout cadence and the fps window.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    /// <summary>The WS-latency histogram's ascending inclusive upper edges (milliseconds); the emission adds the overflow bucket.</summary>
    private static readonly double[] LatencyBoundsMs = [1, 5, 20, 100, 500];

    private readonly IObservable<OrderBook> _bookUpdates = bookUpdates ?? throw new ArgumentNullException(nameof(bookUpdates));
    private readonly Func<int>? _backpressureBacklogProbe = backpressureBacklogProbe;
    private readonly ILogger<PerformanceHudService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>The latency histogram's live counts (one per bucket edge plus the overflow); Interlocked-touched only.</summary>
    private readonly int[] _latencyBuckets = new int[LatencyBoundsMs.Length + 1];

    /// <summary>The emissions lane; silent (and unobserved) for the whole off life.</summary>
    private readonly Subject<PerformanceSnapshot> _snapshots = new();

    /// <summary>The book stream's update count since the previous drain; Interlocked-touched only.</summary>
    private int _bookUpdateCount;

    /// <summary>The running legs, null while off — the off state's zero-trace anchor: everything the sampler owns hangs here and dies with it.</summary>
    private CompositeDisposable? _running;

    /// <summary>
    /// Gets or sets the chart's frame-tap drain (the view glue assigns the
    /// named control's <c>TakeFrameTapCount</c>); null (before the glue)
    /// reads as a quiet chart.
    /// </summary>
    public Func<int>? ChartFrameDrain { get; set; }

    /// <summary>Gets a value indicating whether the sampler runs (the menu checkbox mirrors it through the ViewModel).</summary>
    public bool IsRunning => _running is not null;

    /// <summary>Gets the sampler's 1 Hz emissions while running; never emits off.</summary>
    public IObservable<PerformanceSnapshot> Snapshots => _snapshots.Synchronize();

    /// <summary>
    /// Start the sampler: subscribe the book stream and arm the 1 Hz poll.
    /// A start over a running sampler is a no-op (the menu's double flip
    /// never doubles the legs).
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return;

        _running = new CompositeDisposable(AttachSources(), CreatePollTimer());
        _logger.LogInformation("Performance HUD sampler started");
    }

    /// <summary>
    /// Stop the sampler: every leg (the book subscription, the timer) dies
    /// with the composite — the off state leaves zero traces again.
    /// A stop over an off sampler is a no-op.
    /// </summary>
    public void Stop()
    {
        if (_running is null)
            return;

        _running.Dispose();
        _running = null;
        _logger.LogInformation("Performance HUD sampler stopped");
    }

    /// <summary>
    /// The book stream's tap: count the update and bucket its stamp's age —
    /// the receive-to-consumer pipeline latency. T-142's clock discipline
    /// makes the age transport-only: every adapter stamps its books with the
    /// local receive moment, so <c>UtcNow - Timestamp</c> is how long the
    /// update traveled the pipeline after ingestion — an exchange clock
    /// never enters the computation, and a skewed wall clock shifts both
    /// sides equally. Runs on the stream's thread while running; counters
    /// only.
    /// </summary>
    private void OnBookUpdate(OrderBook book)
    {
        Interlocked.Increment(ref _bookUpdateCount);

        var ageMs = (DateTimeOffset.UtcNow - book.Timestamp).TotalMilliseconds;
        if (ageMs < 0)
            ageMs = 0;

        var bucket = HudMath.HistogramBucketIndex(ageMs, LatencyBoundsMs);
        Interlocked.Increment(ref _latencyBuckets[bucket]);
    }

    /// <summary>
    /// Build and emit one snapshot from the drained counters (the timer's
    /// tick work; the tests drive it directly for determinism). The drains
    /// reset, so the next window samples fresh.
    /// </summary>
    internal void EmitSample()
    {
        var frames = ChartFrameDrain?.Invoke() ?? 0;
        var bookCount = Interlocked.Exchange(ref _bookUpdateCount, 0);
        var drainedBuckets = new int[_latencyBuckets.Length];
        for (var i = 0; i < _latencyBuckets.Length; i++)
            drainedBuckets[i] = Interlocked.Exchange(ref _latencyBuckets[i], 0);

        _snapshots.OnNext(new PerformanceSnapshot(
            HudMath.FramesPerSecond(frames, UpdateInterval.TotalSeconds),
            bookCount,
            Environment.WorkingSet,
            _backpressureBacklogProbe?.Invoke() ?? 0,
            backpressureCapacity,
            drainedBuckets,
            LatencyBoundsMs));
    }

    /// <summary>
    /// The book stream's subscription (internal for the tests' deterministic
    /// drive: the tests attach without the timer and emit their own ticks).
    /// </summary>
    /// <returns>The subscription.</returns>
    internal IDisposable AttachSources() => _bookUpdates.Subscribe(
        OnBookUpdate,
        ex => _logger.LogError(ex, "The performance HUD's book tap stopped unexpectedly"));

    /// <summary>
    /// The 1 Hz poll (internal for the same deterministic-test reason: the
    /// production Start arms it, the tests emit their own ticks).
    /// </summary>
    /// <returns>The timer subscription.</returns>
    private IDisposable CreatePollTimer() => Observable
        .Interval(UpdateInterval)
        .Subscribe(
            _ => EmitSample(),
            ex => _logger.LogError(ex, "The performance HUD's poll stopped unexpectedly"));
}
