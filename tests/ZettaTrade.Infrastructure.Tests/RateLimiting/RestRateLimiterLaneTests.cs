namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The lane partition facts (T-106): the background lane drains only the
/// capped slack, so a queued backfill burst never starves or delays a user
/// submit. At the manual clock the queue scans and admissions are
/// deterministic — a background waiter queued ahead of a user arrival
/// cannot block it, and the user is served first the moment a token flows.
/// </summary>
public sealed class RestRateLimiterLaneTests
{
    #region Fixture

    private const string KlinesLane = "/test/klines";

    /// <summary>
    /// The preemption shape: four-token bucket, half the flow to the
    /// background (slack cap two tokens, background flow two tokens per
    /// interval, user flow two tokens per interval).
    /// </summary>
    private static RestRateLimiterOptions PreemptionShape() => new()
    {
        SettingsKey = "Test",
        ScaleKind = RestBudgetScaleKind.WeightPerMinute,
        BackgroundShare = 0.5,
        Endpoints = [new RestEndpointBudget(KlinesLane, 4, TimeSpan.FromMilliseconds(100))],
    };

    #endregion

    #region Tests: user over background preemption

    /// <summary>
    /// The acceptance pinned at the queue: after draining both pools, the
    /// background burst queues FIRST — and the user submit queued behind it
    /// is still served at its own pool's half-interval flow while the
    /// background's heavier request must wait for its full estimate. The
    /// background's slack and the user's pool never mix, so the user can
    /// never be starved by the burst in front of it.
    /// </summary>
    [Fact]
    public async Task Acquire_UserSubmitQueuedBehindBackgroundIsServedFirst()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(PreemptionShape(), timeProvider: clock);

        // Drain the user pool (four weight-one acquires) and the background
        // slack (a two-token cap).
        for (var index = 0; index < 4; index++)
        {
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        }

        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 2);
        var (mainTokensBefore, slackTokensBefore) = limiter.ProbeTokens(KlinesLane);
        mainTokensBefore.Should().BeLessThanOrEqualTo(0.01, "the user pool is drained");
        slackTokensBefore.Should().BeLessThanOrEqualTo(0.01, "the slack is drained");

        // The background burst queues FIRST (the earliest waiter, two-token
        // weight), then the user submit lands behind it.
        var background = limiter.AcquireAsync(RestLane.Background, KlinesLane, 2);
        await Task.Yield();
        var user = limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        await Task.Yield();

        // One token has flowed into each pool by the user's wake estimate
        // (~52 ms + overshoot), and the user's own pool serves it at once;
        // the background's two-token request still waits for its own pool.
        clock.Advance(TimeSpan.FromMilliseconds(55));

        await FluentActions.Awaiting(() => user.WaitAsync(TimeSpan.FromMilliseconds(200)))
            .Should().NotThrowAsync("the user pool serves the user submit on its own flow, never behind the background's turn");

        await FluentActions.Awaiting(() => background.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .Should().ThrowAsync<TimeoutException>("the background waits out its own heavier estimate; the user never lends it tokens");

        var (mainTokensAfter, slackTokensAfter) = limiter.ProbeTokens(KlinesLane);
        mainTokensAfter.Should().BeInRange(0.0, 0.2, "the user spend landed from the user pool");
        slackTokensAfter.Should().BeGreaterThan(0.5, "the slack held its own accrual for the background's later turn");
    }

    /// <summary>
    /// The background budget is capped: a slack-sized burst may drain the
    /// slack, but the user lane's pool keeps its own flow, so user fills
    /// keep landing at their own cadence even while backgrounds queue.
    /// </summary>
    [Fact]
    public async Task Acquire_BackgroundBurstCannotTouchTheUserPool()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(PreemptionShape(), timeProvider: clock);

        // Drain the background slack cap with two weight-one acquires.
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);
        (_, var slackAfterDrain) = limiter.ProbeTokens(KlinesLane);
        slackAfterDrain.Should().BeLessThanOrEqualTo(0.01, "the slack cap is drained");

        // The user pool is untouched: a user submit still fits at full pool.
        (var mainAfter, _) = limiter.ProbeTokens(KlinesLane);
        mainAfter.Should().Be(4.0, "the background spend came from the slack, not the user pool");

        // The main-pool drain and flow accounting stay clean for the user.
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 4);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().BeLessThanOrEqualTo(0.01, "the user pool refills at its own flow");
    }

    /// <summary>
    /// The whole venue flow is the cap on the two lanes together: within one
    /// refill interval of continuous draining, the user's spend is bounded by
    /// the user pool's flow and the background's by the slack's — the
    /// partition is exact and no pool borrows from the other. The spends
    /// leave behind the flows' leftovers (each pool accrued exactly 2.0 of a
    /// 4.0 budget at the even share), and the probe reads them.
    /// </summary>
    [Fact]
    public async Task Acquire_BothPoolsRefillFromTheSharedFlow()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new RestRateLimiter(PreemptionShape(), timeProvider: clock);

        // Drain both pools to the zero mark: the user lane four weight-one
        // wants, the background one weight-two spend.
        for (var index = 0; index < 4; index++)
        {
            await limiter.AcquireAsync(RestLane.User, KlinesLane, 1);
        }

        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 2);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 2);
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);

        var (mainTokens, slackTokens) = limiter.ProbeTokens(KlinesLane);

        mainTokens.Should().BeInRange(-0.01, 0.01, "the user pool accrued exactly its half of the flow and spent it all");
        slackTokens.Should().BeInRange(0.99, 1.01, "the slack accrued its own half and the weight-one spend left one");
    }

    #endregion
}
