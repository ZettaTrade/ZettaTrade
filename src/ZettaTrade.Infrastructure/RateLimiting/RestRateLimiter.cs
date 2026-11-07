namespace ZettaTrade.Infrastructure.RateLimiting;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The real per-endpoint REST rate limiter (T-106): every adapter-served
/// endpoint family carries a token bucket (budget, refill interval,
/// per-request weights), the <see cref="RestLane.User"/> lane drains the
/// bucket's main pool and the <see cref="RestLane.Background"/> lane drains
/// only a separate capped slack, so a backfill burst borrows its share of
/// the venue flow while a user submit never queues behind it. A reported
/// 429 (the venue's <c>Retry-After</c>) parks every lane for that endpoint
/// and refills the bucket to full after the span.
/// </summary>
/// <remarks>
/// Concurrency discipline: no <c>lock</c> anywhere — each bucket is
/// serialized by its own <see cref="SemaphoreSlim"/> gate, waiter state
/// rides atomic flags, and waiters sleep on the injected
/// <see cref="TimeProvider"/> clock, refilling lazily on access. The
/// <c>Limiter.*</c> settings keys (section documented on
/// <see cref="RestRateLimiterOptions"/>) are read once before the first
/// acquire and then followed reactively — live edits reshape the buckets
/// without a restart; absent keys leave the shipped defaults standing.
/// Within one lane the wait queue is FIFO but not reserve-based: a later
/// small request may slip past an earlier one whose weight deficit does not
/// fit yet. The REST wait is bounded: a park or deficit wait past
/// <see cref="MaxParkedWait"/> is an error, never an infinite stall.
/// </remarks>
public sealed class RestRateLimiter : IRestRateLimiter, IDisposable
{
    #region Constants

    /// <summary>
    /// The bounded-wait ceiling (T-106): a park or a token-deficit wait
    /// longer than this is refused as an error — far above any plausible
    /// <c>Retry-After</c>, so ordinary limiter discipline never trips it.
    /// </summary>
    public static readonly TimeSpan MaxParkedWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The park applied when a 429 arrives without a parsable
    /// <c>Retry-After</c>: the block is real, only the venue's guidance is
    /// missing, so a short default span keeps the lanes honest.
    /// </summary>
    public static readonly TimeSpan DefaultBlockSpan = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The overshoot baked into every wake estimate: a deficit's exact
    /// refill time wakes marginally early under floating-point accrual, and
    /// a returning caller that still misses the fit point would spin. The
    /// quantum guarantees the wake lands strictly past the fit.
    /// </summary>
    public static readonly TimeSpan WakeOvershoot = TimeSpan.FromMilliseconds(2);

    /// <summary>The settings key of the global on/off switch (bool; when off every lane passes unshaped).</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>The settings key of the background share (double; the slack cap and flow share, clamped).</summary>
    public const string BackgroundShareKey = "BackgroundShare";

    /// <summary>The background share's ceiling: the user lane keeps at least a tenth of the flow.</summary>
    private const double MaxBackgroundShare = 0.9;

    /// <summary>Queue ranks: the user lane ranks ahead of the background lane.</summary>
    private const int UserLaneRank = 0;

    /// <summary>The queue rank of the background lane (behind every user waiter).</summary>
    private const int BackgroundLaneRank = 1;

    /// <summary>Waiter state: 1 = admitted (its tokens were spent on its behalf).</summary>
    private const int AdmittedState = 1;

    #endregion

    #region Fields

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RestRateLimiterOptions _options;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ISettingsService? _settings;
    private readonly double _defaultTotalBudget;
    private readonly string _budgetValueKey;
    private readonly IDisposable? _settingsChangesSubscription;
    private Task? _hydrationTask;
    private double _backgroundShare;
    private bool _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Create the limiter from an adapter's shipped manifest. The settings
    /// service is optional (Infrastructure wiring outside the App): when
    /// provided, the <c>Limiter.*</c> keys hydrate once before the first
    /// acquire and then apply reactively; when absent the manifest stands
    /// as-is.
    /// </summary>
    /// <param name="options">The adapter's shipped budget manifest.</param>
    /// <param name="settings">The reactive settings source, when the host provides one.</param>
    /// <param name="timeProvider">The clock (a fake collapses waits in tests; the System clock serves production).</param>
    public RestRateLimiter(
        RestRateLimiterOptions options,
        ISettingsService? settings = null,
        TimeProvider? timeProvider = null)
        : this(options, settings, timeProvider, NullLogger<RestRateLimiter>.Instance)
    {
    }

    /// <summary>
    /// The unrestricted constructor (internal: the facts need a logger the
    /// public shape does not expose).
    /// </summary>
    /// <param name="options">The adapter's shipped budget manifest.</param>
    /// <param name="settings">The reactive settings source, when the host provides one.</param>
    /// <param name="timeProvider">The clock (a fake collapses waits in tests; the System clock serves production).</param>
    /// <param name="logger">The limiter's own logging sink.</param>
    internal RestRateLimiter(
        RestRateLimiterOptions options,
        ISettingsService? settings,
        TimeProvider? timeProvider,
        ILogger logger)
    {
        _options = Validate(options);
        _settings = settings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _backgroundShare = ClampShare(options.BackgroundShare);
        _defaultTotalBudget = _options.Endpoints.Sum(endpoint => endpoint.BudgetTokens);
        _budgetValueKey = $"{_options.SettingsKey}.{BudgetValueKeyFor(_options.ScaleKind)}";

        foreach (var endpoint in _options.Endpoints)
        {
            _buckets[endpoint.EndpointKey] = BuildBucket(endpoint);
        }

        _settingsChangesSubscription = settings?.Changes
            .Where(change => change.Section == RestRateLimiterOptions.SettingsSection)
            .Subscribe(
                OnSettingsChange,
                ex => _logger.LogError(ex, "The Limiter settings stream faulted; the installed budgets keep standing"));
    }

    #endregion

    #region IRestRateLimiter

    /// <inheritdoc/>
    public async Task AcquireAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight),
                weight,
                "A REST request weight must be positive; a zero or negative weight is a call-site error, never a default.");
        }

        if (!_buckets.TryGetValue(endpointKey, out var bucket))
        {
            throw new InvalidOperationException(
                $"No REST budget is configured for endpoint '{endpointKey}' under the '{_options.SettingsKey}' manifest; " +
                "an acquire must name a manifested endpoint family, never an invented one.");
        }

        await HydrateSettingsOnceAsync();

        if (!IsEnabled)
        {
            return; // The operator's global off switch: the lanes pass unshaped.
        }

        var waiter = new Waiter();
        using var cancelledRegistration = ct.Register(
            () =>
            {
                waiter.IsCancelled = true;
                waiter.CycleSignal?.TrySetResult(false);
            });

        try
        {
            while (true)
            {
                var outcome = await EnterOnceAsync(bucket, lane, weight, waiter);
                if (outcome.ImmediateFailure is { } failure)
                {
                    throw failure;
                }

                if (outcome.Admitted)
                {
                    return;
                }

                // The wake-up: the pump completed the cycle signal (an
                // admission, a rewrite that re-plans the wait, the bounded
                // deadline) or the estimate elapsed on the injected clock.
                // Either way the next gate pass re-evaluates from current
                // state, so a reshaped bucket applies to parked waits.
                _ = await Task.WhenAny(outcome.WakeSignal!, Task.Delay(outcome.WaitEstimate!.Value, _timeProvider, ct));

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }
        finally
        {
            waiter.CycleSignal?.TrySetResult(false);
        }
    }

    /// <inheritdoc/>
    public void ReportBlocked(string endpointKey, TimeSpan? retryAfter)
    {
        ThrowIfDisposed();

        if (!_buckets.TryGetValue(endpointKey, out var bucket))
        {
            throw new InvalidOperationException(
                $"No REST budget is configured for endpoint '{endpointKey}' under the '{_options.SettingsKey}' manifest; " +
                "a blocked report must name a manifested endpoint family, never an invented one.");
        }

        var parkSpan = retryAfter switch
        {
            null => DefaultBlockSpan,
            { } span when span <= TimeSpan.Zero => DefaultBlockSpan,
            { } span => span, // The venue's guidance stands as given; the acquire-side bound refuses to wait out a beyond-max park.
        };

        bucket.Gate.WaitAsync();
        try
        {
            bucket.ParkedUntil = _timeProvider.GetUtcNow() + parkSpan;
            WakeAllWaiters(bucket, exclude: null);
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    #endregion

    #region Private Nested Types

    /// <summary>Per-endpoint state: two token pools, the flow rates that refill them and the wait queue between them.</summary>
    private sealed class Bucket
    {
        /// <summary>The bucket's own gate: endpoints are entered one caller at a time; no <c>lock</c> anywhere.</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>The wait queue: the user lane ranks ahead of the background lane, FIFO within a lane.</summary>
        public readonly PriorityQueue<Waiter, (int LaneRank, long Seq)> Waiters = new();

        /// <summary>The manifest key this bucket shapes.</summary>
        public required string EndpointKey { get; init; }

        /// <summary>The manifest refill interval, kept across background reshapes.</summary>
        public required TimeSpan RefillInterval { get; init; }

        /// <summary>Full main-pool capacity (the manifest budget, scaled live by settings).</summary>
        public double MainCapacity { get; set; }

        /// <summary>The background slack cap (the share's slice).</summary>
        public double SlackCapacity { get; set; }

        /// <summary>Main-pool flow rate (tokens per second, the user lane's share of the venue flow).</summary>
        public double MainFlowPerSecond { get; set; }

        /// <summary>Slack flow rate (tokens per second, the background lane's share of the venue flow).</summary>
        public double SlackFlowPerSecond { get; set; }

        /// <summary>Main-pool tokens right now.</summary>
        public double MainTokens { get; set; }

        /// <summary>Slack tokens right now.</summary>
        public double SlackTokens { get; set; }

        /// <summary>Roll-over point of the last accrual step (the injected clock's stamp).</summary>
        public DateTimeOffset LastAccrualUtc { get; set; }

        /// <summary>The active park, when one is standing (a 429's retry guidance).</summary>
        public DateTimeOffset? ParkedUntil { get; set; }

        /// <summary>Monotonic waiter sequence for FIFO ordering within a lane.</summary>
        public long NextSeq { get; set; }

        /// <summary>Fresh-build stamp of the shared bookkeeping.</summary>
        public static Bucket Build(RestEndpointBudget budget, double share, DateTimeOffset now)
        {
            var bucket = new Bucket
            {
                EndpointKey = budget.EndpointKey,
                RefillInterval = budget.RefillInterval,
                MainCapacity = budget.BudgetTokens,
                MainTokens = budget.BudgetTokens,
                LastAccrualUtc = now,
            };
            bucket.ReapplyShare(share, budget.BudgetTokens);
            bucket.SlackTokens = bucket.SlackCapacity;
            return bucket;
        }

        /// <summary>Recompute the slack cap and both flows from the main capacity, the refill interval and the share.</summary>
        public void ReapplyShare(double share, double mainCapacity)
        {
            MainCapacity = mainCapacity;
            SlackCapacity = SlackTokenCeiling(mainCapacity, share);
            // The refill interval is validated positive (Validate), so the
            // division is safe without a floor: a floor here would silently
            // turn fast manifests (sub-second intervals) into a tenth of
            // their flow and park every waiter ten times too long.
            var intervalSeconds = RefillInterval.TotalSeconds;
            MainFlowPerSecond = MainCapacity * (1.0 - share) / intervalSeconds;
            SlackFlowPerSecond = MainCapacity * share / intervalSeconds;
        }

        /// <summary>The slack cap: the ceiling of the share's slice; a zero share mutes the background lane entirely.</summary>
        public static double SlackTokenCeiling(double mainCapacity, double share) =>
            share <= 0.0
                ? 0.0
                : Math.Max(1, Math.Ceiling(share * mainCapacity));
    }

    /// <summary>
    /// One parked acquire: identity, lane rank, budget weight and deadline —
    /// assigned once under the bucket gate at the first enqueue, immutable
    /// afterwards. The cycle signal is replaced per Acquire-loop pass so a
    /// second wake-up is never lost to an already-consumed single-shot task
    /// source.
    /// </summary>
    private sealed class Waiter
    {
        /// <summary>The queue assignment (the lane rank precedes the FIFO sequence).</summary>
        public int LaneRank { get; private set; }

        /// <summary>This waiter's token weight (the request's documented cost).</summary>
        public int Weight { get; private set; }

        /// <summary>The bounded-wait deadline stamp (assigned at first enqueue under the gate).</summary>
        public DateTimeOffset Deadline { get; private set; }

        /// <summary>The FIFO sequence, or -1 while the waiter has not entered the queue.</summary>
        public long QueueSeq { get; private set; } = -1;

        /// <summary>1 = admitted (its tokens were spent on its behalf).</summary>
        public volatile int AdmittedFlag;

        /// <summary>Inline cancellation flag set by the caller's registration (the queue scan skips it).</summary>
        public volatile bool IsCancelled;

        /// <summary>The current wake-up signal, replaced per loop pass (assigned and read under the bucket gate).</summary>
        public volatile TaskCompletionSource<bool>? CycleSignal;

        /// <summary>
        /// Claim the queue slot exactly once (called under the bucket gate);
        /// returns false when the waiter is already queued, so a loop pass
        /// never enqueues it twice.
        /// </summary>
        public bool TryClaimQueueSlot(int laneRank, int weight, long seq, DateTimeOffset deadline)
        {
            if (QueueSeq >= 0)
            {
                return false;
            }

            LaneRank = laneRank;
            Weight = weight;
            QueueSeq = seq;
            Deadline = deadline;
            return true;
        }
    }

    /// <summary>
    /// One gate pass' result: admitted, an immediate failure to throw, or a
    /// sleep plan (the wait estimate bundled with the signal to watch).
    /// </summary>
    private sealed record GateOutcome(
        bool Admitted,
        Exception? ImmediateFailure,
        TimeSpan? WaitEstimate,
        Task? WakeSignal);

    #endregion

    #region Private Properties

    /// <summary>The global on/off switch (the shipped root, hydrated once and kept reactive).</summary>
    private bool IsEnabled { get; set; } = true;

    #endregion

    #region Private Methods

    /// <summary>
    /// One gate pass: accrue, self-fit or queue, and plan the next wake-up.
    /// An already-queued waiter is never enqueued twice; the caller-side
    /// admission flag guards against double spends with the queue scan.
    /// </summary>
    private async Task<GateOutcome> EnterOnceAsync(
        Bucket bucket,
        RestLane lane,
        int weight,
        Waiter waiter)
    {
        await bucket.Gate.WaitAsync();
        try
        {
            if (waiter.AdmittedFlag == AdmittedState)
            {
                // The queue scan admitted this waiter while it was waking.
                return new GateOutcome(Admitted: true, ImmediateFailure: null, WaitEstimate: null, WakeSignal: null);
            }

            Advance(bucket);
            var now = _timeProvider.GetUtcNow();

            if (waiter.AdmittedFlag == AdmittedState)
            {
                // Re-check after the advance: the park-expiry refill's scan
                // (or any in-pass rewrite) may have admitted — and spent for
                // — this waiter while the pass was running; its own fit must
                // not spend a second time.
                return new GateOutcome(Admitted: true, ImmediateFailure: null, WaitEstimate: null, WakeSignal: null);
            }

            if (waiter.QueueSeq >= 0 && now >= waiter.Deadline)
            {
                // The bounded wait is spent: this re-entry must not wait again.
                return new GateOutcome(
                    Admitted: false,
                    ImmediateFailure: new RestRateLimitWaitExpiredException(bucket.EndpointKey, now - waiter.Deadline),
                    WaitEstimate: null,
                    WakeSignal: null);
            }

            if (bucket.ParkedUntil is { } parkedUntil && now < parkedUntil)
            {
                // The venue park holds EVERY lane parked: nothing may fit
                // while it stands, and the arrival only plans the park-end
                // wake (a park past the bounded-wait ceiling is an error,
                // never an infinite stall behind it).
                var parkRemainder = parkedUntil - now;
                if (parkRemainder > MaxParkedWait)
                {
                    return new GateOutcome(
                        Admitted: false,
                        ImmediateFailure: new RestRateLimitWaitExpiredException(bucket.EndpointKey, parkRemainder),
                        WaitEstimate: null,
                        WakeSignal: null);
                }

                if (waiter.TryClaimQueueSlot(
                        lane == RestLane.User ? UserLaneRank : BackgroundLaneRank,
                        weight,
                        bucket.NextSeq++,
                        now + MaxParkedWait))
                {
                    bucket.Waiters.Enqueue(waiter, (waiter.LaneRank, waiter.QueueSeq));
                }

                return new GateOutcome(
                    Admitted: false,
                    ImmediateFailure: null,
                    WaitEstimate: parkRemainder + WakeOvershoot,
                    WakeSignal: (waiter.CycleSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            }

            if (TryFit(bucket, lane, weight))
            {
                // Mark own admission before the scan so the queue drop-up
                // sees the stamp; then wake possibly-fitting waiters behind.
                waiter.AdmittedFlag = AdmittedState;
                ScanAndAdmitQueue(bucket, now);
                return new GateOutcome(Admitted: true, ImmediateFailure: null, WaitEstimate: null, WakeSignal: null);
            }

            var estimate = EstimateWake(bucket, lane, weight, now) + WakeOvershoot;
            if (estimate > MaxParkedWait)
            {
                return new GateOutcome(
                    Admitted: false,
                    ImmediateFailure: new RestRateLimitWaitExpiredException(bucket.EndpointKey, estimate),
                    WaitEstimate: null,
                    WakeSignal: null);
            }

            // The wake-up plan: a fresh signal per pass (a consumed signal
            // never carries a second wake-up).
            waiter.CycleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (waiter.TryClaimQueueSlot(
                    lane == RestLane.User ? UserLaneRank : BackgroundLaneRank,
                    weight,
                    bucket.NextSeq++,
                    now + MaxParkedWait))
            {
                bucket.Waiters.Enqueue(waiter, (waiter.LaneRank, waiter.QueueSeq));
            }

            // A bucket rewrite (this park standing or a reshape) re-plans
            // every OTHER waiter's wait; the caller's own pass already
            // carries the fresh estimate.
            WakeAllWaiters(bucket, exclude: waiter);

            return new GateOutcome(
                Admitted: false,
                ImmediateFailure: null,
                WaitEstimate: estimate,
                WakeSignal: waiter.CycleSignal.Task);
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    /// <summary>Spend the tokens of one lane's pool, when the pool can afford the weight.</summary>
    private static bool TryFit(Bucket bucket, RestLane lane, int weight)
    {
        if (lane == RestLane.User)
        {
            if (bucket.MainTokens >= weight)
            {
                bucket.MainTokens -= weight;
                return true;
            }

            return false;
        }

        if (bucket.SlackTokens >= weight)
        {
            bucket.SlackTokens -= weight;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Advance accrual: a park freezes it, a fresh park expiry resets both
    /// pools to full (the venue's own accountant restarts the window).
    /// </summary>
    private void Advance(Bucket bucket)
    {
        var now = _timeProvider.GetUtcNow();

        if (bucket.ParkedUntil is { } parkedUntil)
        {
            if (now < parkedUntil)
            {
                return; // The park freezes accrual; the expiry refill covers everything after.
            }

            bucket.ParkedUntil = null;
            bucket.MainTokens = bucket.MainCapacity;
            bucket.SlackTokens = bucket.SlackCapacity;
            bucket.LastAccrualUtc = now;
            ScanAndAdmitQueue(bucket, now);
            return;
        }

        var elapsedSeconds = (now - bucket.LastAccrualUtc).TotalSeconds;
        bucket.LastAccrualUtc = now;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        bucket.MainTokens = Math.Min(
            bucket.MainCapacity,
            bucket.MainTokens + bucket.MainFlowPerSecond * elapsedSeconds);
        bucket.SlackTokens = Math.Min(
            bucket.SlackCapacity,
            bucket.SlackTokens + bucket.SlackFlowPerSecond * elapsedSeconds);
    }

    /// <summary>
    /// The queue scan: admit waiting acquires in lane-priority and FIFO
    /// order while their pools can afford them, drop cancelled, expired,
    /// settled and already-admitted entries, then re-queue survivors in
    /// order.
    /// </summary>
    private void ScanAndAdmitQueue(Bucket bucket, DateTimeOffset now)
    {
        if (bucket.Waiters.Count == 0)
        {
            return;
        }

        var survivors = new List<(Waiter Waiter, (int LaneRank, long Seq) Priority)>();
        while (bucket.Waiters.TryDequeue(out var waiter, out var priority))
        {
            if (waiter.AdmittedFlag == AdmittedState)
            {
                // The self-fit already spent the tokens; only the entry lingers.
                CompleteCycleSignal(waiter);
                continue;
            }

            if (waiter.IsCancelled)
            {
                CompleteCycleSignal(waiter);
                continue;
            }

            if (now >= waiter.Deadline)
            {
                _logger.LogWarning(
                    "The REST limiter dropped a waiter at endpoint {EndpointKey}: the bounded wait expired",
                    bucket.EndpointKey);
                CompleteCycleSignal(waiter);
                continue;
            }

            var lane = priority.LaneRank == UserLaneRank ? RestLane.User : RestLane.Background;
            if (TryFit(bucket, lane, waiter.Weight))
            {
                waiter.AdmittedFlag = AdmittedState;
                CompleteCycleSignal(waiter);
                continue;
            }

            survivors.Add((waiter, priority));
        }

        foreach (var (survivor, priority) in survivors)
        {
            bucket.Waiters.Enqueue(survivor, priority);
        }
    }

    /// <summary>Complete the waiter's cycle signal (safe on consumed or replaced signals).</summary>
    private static void CompleteCycleSignal(Waiter waiter) =>
        waiter.CycleSignal?.TrySetResult(false);

    /// <summary>
    /// A bucket rewrite (a park change or a reshape) wakes every queued
    /// waiter so the next gate pass re-plans; the calling waiter (already
    /// carrying its fresh estimate) may be excluded.
    /// </summary>
    private void WakeAllWaiters(Bucket bucket, Waiter? exclude)
    {
        foreach (var (waiter, _) in bucket.Waiters.UnorderedItems)
        {
            if (!ReferenceEquals(waiter, exclude))
            {
                CompleteCycleSignal(waiter);
            }
        }
    }

    /// <summary>
    /// The next wake estimate on the lane's pool: the deficit's refill time
    /// at that pool's flow (the caller adds the wake overshoot).
    /// </summary>
    /// <remarks>Park-standing arrivals never reach this: the park branch in the gate pass plans the park-end wake itself.</remarks>
    private TimeSpan EstimateWake(
        Bucket bucket,
        RestLane lane,
        int weight,
        DateTimeOffset now)
    {
        var tokens = lane == RestLane.User ? bucket.MainTokens : bucket.SlackTokens;
        var flowPerSecond = lane == RestLane.User
            ? bucket.MainFlowPerSecond
            : bucket.SlackFlowPerSecond;

        var remainingWeight = weight - tokens;
        if (remainingWeight <= 0)
        {
            // Unreachable after a failed fit under the same gate: the
            // invariant documents it, the return keeps future callers safe.
            return TimeSpan.Zero;
        }

        if (flowPerSecond <= 0)
        {
            // No flow and a standing deficit: this can never fit, so the
            // bounded-error path reports it immediately.
            return MaxParkedWait + MaxParkedWait;
        }

        return TimeSpan.FromSeconds(remainingWeight / flowPerSecond);
    }

    /// <summary>Build one new bucket from its manifest entry with the share applied.</summary>
    private Bucket BuildBucket(RestEndpointBudget budget) =>
        Bucket.Build(budget, _backgroundShare, _timeProvider.GetUtcNow());

    /// <summary>Apply the background share to every bucket (live; clamped; standing tokens clamp on the new ceiling).</summary>
    private void ApplyShare(double rawShare)
    {
        _backgroundShare = ClampShare(rawShare);

        foreach (var bucket in _buckets.Values)
        {
            bucket.Gate.WaitAsync();
            try
            {
                bucket.ReapplyShare(_backgroundShare, bucket.MainCapacity);
                bucket.SlackTokens = Math.Min(bucket.SlackTokens, bucket.SlackCapacity);
                bucket.MainTokens = Math.Min(bucket.MainTokens, bucket.MainCapacity);
                WakeAllWaiters(bucket, exclude: null);
            }
            finally
            {
                bucket.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Apply the per-adapter budget key: a weight-token manifest is
    /// rescaled proportionally from the shipped total, a request-count
    /// manifest is re-based per its own default budgets.
    /// </summary>
    private void ApplyBudgetValue(int budgetValue)
    {
        if (budgetValue <= 0)
        {
            _logger.LogWarning(
                "The Limiter budget key '{BudgetValueKey}' arrived with a non-positive value; the shipped budgets keep standing",
                _budgetValueKey);
            return;
        }

        switch (_options.ScaleKind)
        {
            case RestBudgetScaleKind.WeightPerMinute when _defaultTotalBudget > 0:
                {
                    var factor = budgetValue / _defaultTotalBudget;
                    foreach (var bucket in _buckets.Values)
                    {
                        ScaleCapacity(bucket, factor);
                    }

                    break;
                }

            case RestBudgetScaleKind.RequestsPerSecond:
                {
                    foreach (var endpoint in _options.Endpoints)
                    {
                        ScaleCapacity(_buckets[endpoint.EndpointKey], budgetValue / (double)endpoint.BudgetTokens);
                    }

                    break;
                }
        }
    }

    /// <summary>Reshape one bucket's capacity by the factor; the standing tokens and the flows re-derive.</summary>
    private void ScaleCapacity(Bucket bucket, double factor)
    {
        if (factor <= 0)
        {
            return;
        }

        bucket.Gate.WaitAsync();
        try
        {
            var newCapacity = Math.Max(1, Math.Round(bucket.MainCapacity * factor, MidpointRounding.AwayFromZero));
            bucket.ReapplyShare(_backgroundShare, newCapacity);
            bucket.MainTokens = Math.Min(bucket.MainTokens, newCapacity);
            bucket.SlackTokens = Math.Min(bucket.SlackTokens, bucket.SlackCapacity);
            WakeAllWaiters(bucket, exclude: null);
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    /// <summary>Read the Limiter settings keys once, before the first acquire (concurrent acquires share one pass).</summary>
    private Task HydrateSettingsOnceAsync()
    {
        if (_settings is null)
        {
            return Task.CompletedTask;
        }

        if (_hydrationTask is { } inFlight)
        {
            return inFlight;
        }

        var hydration = HydrateCoreAsync();
        var previous = Interlocked.CompareExchange(ref _hydrationTask, hydration, null);
        if (previous is not null)
        {
            return previous; // A concurrent first acquire won the slot.
        }

        return hydration;
    }

    /// <summary>The hydration itself: the on/off switch, the background share and the per-adapter budget key.</summary>
    private async Task HydrateCoreAsync()
    {
        IsEnabled = await _settings!.GetAsync(RestRateLimiterOptions.SettingsSection, EnabledKey, true);

        // Only a PERSISTED share reshapes the shipped buckets: an absent key
        // leaves the manifest's own share standing (the settings service is
        // optional context, not an authority over shipped defaults).
        var storedShare = await _settings.GetAsync<double?>(
            RestRateLimiterOptions.SettingsSection,
            BackgroundShareKey,
            defaultValue: null);
        if (storedShare is { } hydratedShare)
        {
            ApplyShare(hydratedShare);
        }

        var stored = await _settings.GetAsync<object?>(
            RestRateLimiterOptions.SettingsSection,
            _budgetValueKey,
            null);
        var budgetValue = ToDouble(stored);
        if (!double.IsNaN(budgetValue))
        {
            ApplyBudgetValue((int)Math.Round(budgetValue, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Routing for one reactive settings change (only this limiter's own keys apply).</summary>
    private void OnSettingsChange(SettingsChange change)
    {
        if (_disposed)
        {
            return;
        }

        if (change.Key == EnabledKey && change.NewValue is bool enabled)
        {
            IsEnabled = enabled;
            return;
        }

        if (change.Key == BackgroundShareKey && change.NewValue is not null)
        {
            ApplyShare(ToDouble(change.NewValue));
            return;
        }

        if (change.Key == _budgetValueKey && change.NewValue is not null)
        {
            var budgetValue = ToDouble(change.NewValue);
            if (!double.IsNaN(budgetValue))
            {
                ApplyBudgetValue((int)Math.Round(budgetValue, MidpointRounding.AwayFromZero));
            }
        }
    }

    /// <summary>The settings round-trip may store a number as an int, a long or a double; unwrap safely.</summary>
    private static double ToDouble(object? value) => value switch
    {
        int number => number,
        long number => number,
        double number => number,
        bool flag => flag ? 1.0 : 0.0,
        null => double.NaN,
        _ => double.NaN,
    };

    /// <summary>The share clamp: [0, 0.9] — the ceiling keeps the user lane a floor of the flow; NaN falls back to the shipped default.</summary>
    private static double ClampShare(double share) =>
        double.IsNaN(share) || double.IsInfinity(share)
            ? RestRateLimiterOptions.DefaultBackgroundShare
            : Math.Clamp(share, 0.0, MaxBackgroundShare);

    /// <summary>The per-adapter settings key of the budget value, by the manifest's scale kind.</summary>
    private static string BudgetValueKeyFor(RestBudgetScaleKind scaleKind) => scaleKind switch
    {
        RestBudgetScaleKind.WeightPerMinute => "RequestWeightMinute",
        RestBudgetScaleKind.RequestsPerSecond => "RequestsPerSecond",
        _ => throw new ArgumentOutOfRangeException(nameof(scaleKind)),
    };

    /// <summary>Validate the manifest shape: unique endpoint keys, positive budgets and intervals.</summary>
    private static RestRateLimiterOptions Validate(RestRateLimiterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SettingsKey))
        {
            throw new ArgumentException("The limiter manifest must carry a settings key.", nameof(options));
        }

        if (options.Endpoints.Count == 0)
        {
            throw new ArgumentException("The limiter manifest must carry at least one endpoint budget.", nameof(options));
        }

        if (options.Endpoints
                .Select(endpoint => endpoint.EndpointKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != options.Endpoints.Count)
        {
            throw new ArgumentException(
                "The limiter manifest carries duplicate endpoint keys; one bucket per endpoint family.",
                nameof(options));
        }

        foreach (var endpoint in options.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.EndpointKey))
            {
                throw new ArgumentException("An endpoint budget must carry a non-empty endpoint key.", nameof(options));
            }

            if (endpoint.BudgetTokens <= 0)
            {
                throw new ArgumentException(
                    $"The '{endpoint.EndpointKey}' budget must be positive; a zero budget cannot meter.",
                    nameof(options));
            }

            if (endpoint.RefillInterval <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"The '{endpoint.EndpointKey}' refill interval must be positive.",
                    nameof(options));
            }
        }

        return options;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Facts-only disclosure (internal, tests read it through the visible-to
    /// seam): the current token pools of one endpoint bucket, without
    /// touching accrual.
    /// </summary>
    /// <param name="endpointKey">The manifested endpoint key.</param>
    internal (double MainTokens, double SlackTokens) ProbeTokens(string endpointKey) =>
        _buckets.TryGetValue(endpointKey, out var bucket)
            ? (bucket.MainTokens, bucket.SlackTokens)
            : throw new InvalidOperationException($"No bucket is configured for '{endpointKey}'.");

    #endregion

    #region IDisposable

    /// <summary>Release the settings subscription and the bucket gates; further acquires fail fast.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsChangesSubscription?.Dispose();

        foreach (var bucket in _buckets.Values)
        {
            bucket.Gate.Dispose();
        }

        _buckets.Clear();
    }

    #endregion
}
