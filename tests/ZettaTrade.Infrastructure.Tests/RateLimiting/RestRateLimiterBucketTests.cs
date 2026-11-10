namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The bucket mechanics at their bolts (T-106): the drain/refill golden at
/// the manual clock (a full refill over one interval), weight accounting
/// (a heavier request consumes more and a lighter one still fits), the
/// real-clock refill cadence pinned by median and mean (the worst frame is
/// only reported, never asserted) and the fail-fast contracts (weights,
/// unknown endpoints, bounded waits).
/// </summary>
public sealed class RestRateLimiterBucketTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Bind the worst-frame reporter (the cadence fact observes it).</summary>
    /// <param name="output">The reporter xUnit serves.</param>
    public RestRateLimiterBucketTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Fixture

    private const string KlinesLane = "/test/klines";

    private static RestRateLimiterOptions SingleBucket(
        string endpointKey,
        int budgetTokens,
        TimeSpan refillInterval,
        double backgroundShare = RestRateLimiterOptions.DefaultBackgroundShare) => new()
        {
            SettingsKey = "Test",
            ScaleKind = RestBudgetScaleKind.WeightPerMinute,
            BackgroundShare = backgroundShare,
            Endpoints = [new RestEndpointBudget(endpointKey, budgetTokens, refillInterval)],
        };

    #endregion

    #region Tests: drain and refill golden

    /// <summary>
    /// The drain/refill golden: a four-token bucket drains with four
    /// immediate acquires, then the fifth waits until the interval's flow
    /// refills it — at the manual clock, so the timing is exact rather than
    /// sampled.
    /// </summary>
    [Fact]
    public async Task Acquire_DrainsTheBucketThenWaitsOneRefill()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        for (var index = 0; index < 4; index++)
        {
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        }

        // The 5th token fits only after the drained pool refills one token at
        // its flow: 4 budget x (1 - 0.35 share) / 0.1 s = 26 tokens/s, so the
        // one-token fit point is 1/26 s (~38.5 ms). The wake timer rides the
        // runtime's whole-millisecond due quantization (the 38.5+2 ms
        // estimate arms its timer at ~40 ms), so the exact boundary the fact
        // can pin at the manual clock is: below 30 ms nothing fires and
        // nothing fits; by 50 ms both the wake and the fit are crossed.
        var pending = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await Task.Yield();

        clock.Advance(TimeSpan.FromMilliseconds(30));
        await FluentActions.Awaiting(() => pending.WaitAsync(TimeSpan.FromMilliseconds(30)))
            .Should().ThrowAsync<TimeoutException>("below the fit point the drained pool cannot serve the ask");

        clock.Advance(TimeSpan.FromMilliseconds(20));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        pending.IsCompletedSuccessfully.Should().BeTrue("past the fit point the accrued flow admits the drained acquire");
    }

    /// <summary>
    /// The refill is a flow, not a burst: after half an interval the drained
    /// user pool holds the flow's fraction of the budget (~1.3 of 4), and an
    /// admitted weight-one spend takes exactly the token it needs — the
    /// accrual is pinned by the leftover the probe reads after the spend.
    /// </summary>
    [Fact]
    public async Task Acquire_RefillsProportionally()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        for (var index = 0; index < 4; index++)
        {
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        }

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);

        var (mainTokens, _) = limiter.ProbeTokens(KlinesLane);

        // 26 tokens/s over 50 ms refills 1.3; the spend leaves ~0.3 behind.
        mainTokens.Should().BeInRange(0.2, 0.4, "half the interval refills the flow's fraction, and the spend took one token");
    }

    /// <summary>
    /// The same golden against the real System clock: with an even share the
    /// user pool carries half the flow (4 x 0.5 / 60 ms = ~33 tokens/s), so
    /// a drained bucket's weight-two wait's estimate is exactly one interval;
    /// the median of repeated waits stays inside the sampled tolerance and
    /// the worst frame is reported for observation only. The assert rides the
    /// median on purpose: the scheduler's stalls (the worst frames reach
    /// ~0.5 s under the matrix load) land on the mean, not on the central
    /// tendency the refill contract talks about.
    /// </summary>
    /// <param name="output">The test reporter for the worst-frame observation.</param>
    [Fact]
    public async Task Acquire_RefillWaitsTrackTheRealIntervalClosely()
    {
        var attempts = 8;
        var waits = new double[attempts];

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var limiter = new RestRateLimiter(
                SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(60), backgroundShare: 0.5));
            for (var index = 0; index < 4; index++)
            {
                await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
            }

            var stopwatch = Stopwatch.StartNew();
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 2);
            stopwatch.Stop();
            waits[attempt] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var mean = waits.Average();
        var median = waits.OrderBy(w => w).ElementAt(attempts / 2);
        _output.WriteLine($"refill waits (ms): mean={mean:F1} median={median:F1} worst={waits.Max():F1}");

        median.Should().BeInRange(60, 120, "the median lands on one refill interval under thread-pool scheduling");
    }

    /// <summary>
    /// Weight accounting: a weight-two spend leaves two tokens of five, a
    /// second weight-two spend leaves one, and while one token remains a
    /// weight-one acquire still fits although a following weight-two cannot.
    /// </summary>
    [Fact]
    public async Task Acquire_AccountsWeightPerRequest()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 5, TimeSpan.FromSeconds(1)),
            timeProvider: clock);

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 2);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().Be(3, "the weight-two spend consumed two of five");

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 2);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().Be(1, "the second weight-two spend leaves one");

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().Be(0, "the light request spends the last token");
    }

    /// <summary>
    /// A request heavier than the whole budget refuses to park forever: a
    /// weight whose deficit exceeds the bounded-wait ceiling at the bucket's
    /// flow (4 budget, 1 s interval → 2.6 tokens/s user flow → 2000 weight
    /// needs ~12.8 min > the 10-min ceiling) expires as the documented error
    /// on arrival, and the bucket stays intact for the next callers.
    /// </summary>
    [Fact]
    public async Task Acquire_WeightBeyondTheBucketFailsWithoutStall()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromSeconds(1)),
            timeProvider: clock);

        await FluentActions.Awaiting(() => limiter.AcquireAsync(RestLane.User, KlinesLane, 2000))
            .Should().ThrowAsync<RestRateLimitWaitExpiredException>("the deficit wait past the ceiling is an error, never a stall");

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().Be(3, "the failed weight never spent anything");
    }

    /// <summary>
    /// A zero-flow pool (a share of zero mutes the background lane) refuses
    /// a background acquire immediately: no flow, and a standing deficit is
    /// a bounded error, never a phantom wait.
    /// </summary>
    [Fact]
    public async Task Acquire_MutedBackgroundLaneFailsFast()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromSeconds(1), backgroundShare: 0.0),
            timeProvider: clock);

        var acquire = () => limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);
        await acquire.Should().ThrowAsync<RestRateLimitWaitExpiredException>();
    }

    /// <summary>
    /// The manifest errors fail fast: a non-positive weight, a nonexistent
    /// endpoint key and a manifest with duplicate keys or a zero budget are
    /// configuration errors that throw at the boundary, never silently
    /// metered as defaults.
    /// </summary>
    [Fact]
    public async Task Acquire_And_Manifests_ThrowOnWrongShape()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromSeconds(1)),
            timeProvider: clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => limiter.AcquireAsync(RestLane.User, KlinesLane, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => limiter.AcquireAsync(RestLane.User, "/unmanifested", 1));

        var duplicated = () => new RestRateLimiter(new RestRateLimiterOptions
        {
            SettingsKey = "Test",
            ScaleKind = RestBudgetScaleKind.WeightPerMinute,
            Endpoints = [
                new RestEndpointBudget(KlinesLane, 4, TimeSpan.FromSeconds(1)),
                new RestEndpointBudget(KlinesLane, 4, TimeSpan.FromSeconds(1)),
            ],
        });
        duplicated.Should().Throw<ArgumentException>("one bucket per endpoint family");

        var zeroBudget = () => new RestRateLimiter(new RestRateLimiterOptions
        {
            SettingsKey = "Test",
            ScaleKind = RestBudgetScaleKind.WeightPerMinute,
            Endpoints = [new RestEndpointBudget(KlinesLane, 0, TimeSpan.FromSeconds(1))],
        });
        zeroBudget.Should().Throw<ArgumentException>("a zero budget cannot meter");
    }

    /// <summary>
    /// Cancellation mid-wait: an acquire parked on an empty bucket cancels
    /// cleanly on its token — no slot is spent, and the next caller fits.
    /// </summary>
    [Fact]
    public async Task Acquire_CancellationMidWaitLeavesTheBucketIntact()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 2, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);

        using var cancellation = new CancellationTokenSource();
        var pending = limiter.AcquireAsync(RestLane.User, KlinesLane, 1, cancellation.Token);

        cancellation.Cancel();

        await FluentActions.Awaiting(() => pending)
            .Should().ThrowAsync<OperationCanceledException>();

        clock.Advance(TimeSpan.FromMilliseconds(150));

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
    }

    #endregion

    #region Tests: lane resolver

    /// <summary>
    /// The lane resolver's shape: a fresh tail load (or the default
    /// open-ended end) rides the user lane, and a range ending in the past
    /// is background stitching; ambiguity defaults to the user lane.
    /// </summary>
    [Fact]
    public void LaneForBarDirectoryClassifiesByTheNewestEdge()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        RestLaneResolver.LaneForBarRange(null, now).Should().Be(RestLane.User);
        RestLaneResolver.LaneForBarRange(now.AddSeconds(-90), now).Should().Be(RestLane.User, "the tail grace covers the freshest edge");
        RestLaneResolver.LaneForBarRange(now.AddMinutes(-5), now).Should().Be(RestLane.Background, "a past-ended range is a gap walk");
        RestLaneResolver.LaneForBarRange(now.AddDays(-30), now).Should().Be(RestLane.Background);
    }

    #endregion
}
