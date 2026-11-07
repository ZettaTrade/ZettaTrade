namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The park contract (T-106): a reported 429 parks EVERY lane for the
/// endpoint, the venue's <c>Retry-After</c> span is honored as given, the
/// limiter's default block span covers a missing or nonsensical guidance,
/// the park's expiry refills both pools to full (the venue's own accountant
/// restarts the window), a park remainder past the bounded-wait ceiling is
/// an error — never an infinite stall — and a waiter cancelled mid-park
/// leaves no debt behind.
/// </summary>
public sealed class RestRateLimiterParkTests
{
    #region Fixture

    private const string KlinesLane = "/test/klines";

    private static RestRateLimiterOptions SingleBucket(
        string endpointKey,
        int budgetTokens,
        TimeSpan refillInterval) => new()
        {
            SettingsKey = "Test",
            ScaleKind = RestBudgetScaleKind.WeightPerMinute,
            BackgroundShare = RestRateLimiterOptions.DefaultBackgroundShare,
            Endpoints = [new RestEndpointBudget(endpointKey, budgetTokens, refillInterval)],
        };

    #endregion

    #region Tests: the Retry-After park

    /// <summary>
    /// The venue's guidance is honored across both lanes exactly: a 100 ms
    /// Retry-After parks the user waiter for the full span (a drained pool
    /// would otherwise admit it at once), the expiry refills both pools to
    /// full, and the queued waiters — the user ahead of the background — are
    /// admitted out of their own refilled pools.
    /// </summary>
    /// <remarks>
    /// Both waiters queue behind one park: the user's pool was drained
    /// beforehand, so the only admission route is the park's expiry refill.
    /// </remarks>
    [Fact]
    public async Task ReportBlocked_ParkHonoredAcrossLanesAndRefillsFull()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        for (var index = 0; index < 4; index++)
        {
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        }

        var (mainBefore, _) = limiter.ProbeTokens(KlinesLane);
        mainBefore.Should().BeLessThanOrEqualTo(0.01, "the user pool is drained");

        limiter.ReportBlocked(KlinesLane, TimeSpan.FromMilliseconds(100));

        var user = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        var background = limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);
        await Task.Yield();

        // Mid-park, neither lane is served while the span stands.
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await FluentActions.Awaiting(() => user.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("the park holds the user lane while its span stands");
        await FluentActions.Awaiting(() => background.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("the park holds the background lane while its span stands");

        // Crossing the park's end wakes the waiters; the expiry refilled
        // both pools to full and the scan admits them out of their own pools.
        clock.Advance(TimeSpan.FromMilliseconds(110));

        await FluentActions.Awaiting(() => user.WaitAsync(TimeSpan.FromMilliseconds(200)))
            .Should().NotThrowAsync("the park's expiry refills full and admits the queued user waiter");
        await FluentActions.Awaiting(() => background.WaitAsync(TimeSpan.FromMilliseconds(200)))
            .Should().NotThrowAsync("the park's expiry refills full and admits the queued background waiter");

        var (mainAfter, slackAfter) = limiter.ProbeTokens(KlinesLane);
        mainAfter.Should().Be(3.0, "the expiry refilled the drained pool to full and the user spend took one");
        slackAfter.Should().Be(1.0, "the slack also reset to full and the background spend took one");
    }

    /// <summary>
    /// A missing or nonsensical guidance is not a free pass: a null
    /// <c>Retry-After</c> parks for the limiter's default block span, and the
    /// queued waiter is still parked just before the span's end.
    /// </summary>
    [Fact]
    public async Task ReportBlocked_MissingRetryAfter_AppliesTheDefaultBlockSpan()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        limiter.ReportBlocked(KlinesLane, null);

        var parked = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await Task.Yield();

        clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(50));
        await FluentActions.Awaiting(() => parked.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("the default span's park still stands just before its end");

        clock.Advance(TimeSpan.FromMilliseconds(150));
        await FluentActions.Awaiting(() => parked.WaitAsync(TimeSpan.FromMilliseconds(200)))
            .Should().NotThrowAsync("the default span expired and the waiter wakes out of the full refill");
    }

    /// <summary>
    /// A non-positive Retry-After is the same "the block is real, only the
    /// guidance is missing" shape: it parks for the default span too — it
    /// never acts as an already-expired park and admits at once.
    /// </summary>
    [Fact]
    public async Task ReportBlocked_NonPositiveRetryAfter_IsNeverAnExpiredPark()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        limiter.ReportBlocked(KlinesLane, TimeSpan.FromSeconds(-1));

        var parked = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await Task.Yield();

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await FluentActions.Awaiting(() => parked.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("a non-positive guidance still parks for the real default span");
    }

    /// <summary>
    /// A venue park longer than the bounded-wait ceiling is refused on
    /// arrival: the acquire surfaces the documented bounded error — it never
    /// parks for an unbounded span.
    /// </summary>
    [Fact]
    public async Task ReportBlocked_LongerThanTheMaxParkSpan_FailsTheAcquireImmediately()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        limiter.ReportBlocked(KlinesLane, RestRateLimiter.MaxParkedWait + TimeSpan.FromMinutes(1));

        await FluentActions.Awaiting(() => limiter.AcquireAsync(RestLane.User, KlinesLane, 1))
            .Should().ThrowAsync<RestRateLimitWaitExpiredException>(
                "a park remainder past the ceiling is an immediate bounded error");
    }

    /// <summary>
    /// A waiter cancelled mid-park throws on its token and leaves nothing
    /// behind: the next waiter meets the park honestly — it is not admitted
    /// while the span stands (the cancelled waiter freed no tokens that its
    /// successor could have borrowed).
    /// </summary>
    [Fact]
    public async Task Acquire_CancellationMidParkLeavesNoDebtBehind()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            timeProvider: clock);

        limiter.ReportBlocked(KlinesLane, TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var parked = limiter.AcquireAsync(RestLane.User, KlinesLane, 1, cancellation.Token);
        await Task.Yield();

        cancellation.Cancel();
        await FluentActions.Awaiting(() => parked)
            .Should().ThrowAsync<OperationCanceledException>("the acquire honors the caller's token mid-park");

        var successor = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await Task.Yield();
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await FluentActions.Awaiting(() => successor.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("the park is not a queue slot the canceller freed");
    }

    #endregion
}
