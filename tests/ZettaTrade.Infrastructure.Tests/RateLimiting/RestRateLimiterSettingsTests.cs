namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The settings contract of the limiter (T-106): the <c>Limiter.*</c> keys
/// hydrate once before the first acquire (concurrent acquires share the one
/// pass — the read count pins it), persisted-before-start values shape the
/// buckets through that hydration, the reactive stream applies live edits
/// without an acquire and without a restart, the global off switch lets
/// every lane pass unshaped (nothing is spent), and a non-positive budget
/// key is ignored with the shipped buckets standing.
/// </summary>
public sealed class RestRateLimiterSettingsTests
{
    #region Fixture

    private const string KlinesLane = "/test/klines";

    private static RestRateLimiterOptions SingleBucket(
        string endpointKey,
        int budgetTokens,
        TimeSpan refillInterval,
        double backgroundShare = RestRateLimiterOptions.DefaultBackgroundShare,
        string settingsKey = "Test",
        RestBudgetScaleKind scaleKind = RestBudgetScaleKind.WeightPerMinute) => new()
        {
            SettingsKey = settingsKey,
            ScaleKind = scaleKind,
            BackgroundShare = backgroundShare,
            Endpoints = [new RestEndpointBudget(endpointKey, budgetTokens, refillInterval)],
        };

    #endregion

    #region Tests: the hydration-once pass

    /// <summary>
    /// Values persisted before the limiter existed shape the buckets through
    /// the first acquire's hydration pass (the reactive change was emitted
    /// before anyone subscribed — pre-start persistence is the only route),
    /// and the pass is shared: the read count is exactly the pass's size and
    /// holds steady across later acquires.
    /// </summary>
    [Fact]
    public async Task Acquire_HydratesThePersistedKeysOnceBeforeTheFirstGate()
    {
        var clock = new ManualTimeProvider();
        using var settings = new TestSettingsService();
        await settings.SetAsync(
            RestRateLimiterOptions.SettingsSection,
            RestRateLimiter.BackgroundShareKey,
            0.75);

        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            settings: settings,
            timeProvider: clock);

        // The first acquire runs the hydration: the seeded share shapes the
        // slack cap (3) and flow (30/s) while the buckets still hold the
        // built tokens (2).
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 1);

        // The one pass read the switch, the share and the budget key: three
        // reads, no more on later acquires.
        settings.Reads.Should().Be(3, "the hydration pass reads exactly the switch, the share and the budget key once");

        // The seeded cap now allows a background spend a shipped-share cap
        // (two tokens) could never afford: half an interval refills the flow
        // (30/s x 0.1 s = 3) past the weight-two ask.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 2);

        settings.Reads.Should().Be(3, "later acquires reuse the completed pass, never a second hydration");
        limiter.ProbeTokens(KlinesLane).SlackTokens.Should().BeInRange(0.9, 1.1, "the capped-at-three slack spent its weight-two ask");
    }

    #endregion

    #region Tests: the reactive stream

    /// <summary>
    /// The reactive stream reshapes the buckets live: dropping the background
    /// share to zero mutes the background lane without any new acquire (the
    /// next one fails the bounded-error way), and raising the share gives the
    /// lane a slack whose flow the shipped share never allowed.
    /// </summary>
    [Fact]
    public async Task SettingsChange_ShareRewrites_MuteAndUnmuteTheBackgroundLaneLive()
    {
        var clock = new ManualTimeProvider();
        using var settings = new TestSettingsService();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            settings: settings,
            timeProvider: clock);

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.BackgroundShareKey, 0.0);

        await FluentActions.Awaiting(() => limiter.AcquireAsync(RestLane.Background, KlinesLane, 1))
            .Should().ThrowAsync<RestRateLimitWaitExpiredException>("a zero-flow slack cannot ever afford the ask");

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.BackgroundShareKey, 0.75);

        var unmuted = limiter.AcquireAsync(RestLane.Background, KlinesLane, 3);
        await Task.Yield();

        // The raised slack flows 30 tokens/s: the weight-three ask fits right
        // after one interval of accrual at the manual clock.
        clock.Advance(TimeSpan.FromMilliseconds(110));
        await unmuted.WaitAsync(TimeSpan.FromMilliseconds(200));
        unmuted.IsCompletedSuccessfully.Should().BeTrue("the live share rewrite gave the slack its own flow");
        limiter.ProbeTokens(KlinesLane).SlackTokens.Should().BeInRange(-0.01, 0.01, "the weight-three ask spent the refilled slack");
    }

    /// <summary>
    /// The global off switch applies live: an acquire gates nothing and
    /// spends nothing, and the buckets keep standing for the moment the
    /// switch returns.
    /// </summary>
    [Fact]
    public async Task SettingsChange_EnabledFalse_LetsEveryLanePassUnshaped()
    {
        var clock = new ManualTimeProvider();
        using var settings = new TestSettingsService();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            settings: settings,
            timeProvider: clock);

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.EnabledKey, false);
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 9);
        await limiter.AcquireAsync(RestLane.Background, KlinesLane, 9);

        var (mainTokens, slackTokens) = limiter.ProbeTokens(KlinesLane);
        mainTokens.Should().Be(4.0, "the off switch gates nothing and spends nothing");
        slackTokens.Should().Be(2.0, "the background lane passes unshaped too");

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.EnabledKey, true);

        // Re-enabled, the standing buckets serve the lanes honestly: the
        // weight-four user ask spends the whole pool.
        await limiter.AcquireAsync(RestLane.User, KlinesLane, 4);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should().
            BeLessThanOrEqualTo(0.01, "the re-enabled limiter meters from the standing buckets");
    }

    /// <summary>
    /// The per-adapter budget key reshapes each scale dialect: a
    /// weight-per-minute count rescales the buckets proportionally (an
    /// emptied user pool refills the doubled budget within one interval — it
    /// could not within the shipped capacity), and a per-second count re-bases
    /// each bucket by its own shipped value (the standing tokens clamp at
    /// once, no accrual needed).
    /// </summary>
    [Fact]
    public async Task SettingsChange_BudgetKeys_RescaleEachScaleDialect()
    {
        var clock = new ManualTimeProvider();
        using var settings = new TestSettingsService();
        using var weightMinute = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100), backgroundShare: 0.5),
            settings: settings,
            timeProvider: clock);
        using var perSecond = new RestRateLimiter(
            SingleBucket(
                KlinesLane,
                10,
                TimeSpan.FromSeconds(1),
                settingsKey: "Solo",
                scaleKind: RestBudgetScaleKind.RequestsPerSecond),
            settings: settings,
            timeProvider: clock);

        for (var index = 0; index < 4; index++)
        {
            await weightMinute.AcquireAsync(RestLane.User, KlinesLane, 1);
        }
        await perSecond.AcquireAsync(RestLane.Background, KlinesLane, 1);

        await settings.SetAsync(
            RestRateLimiterOptions.SettingsSection,
            "Test.RequestWeightMinute",
            8);
        await settings.SetAsync(
            RestRateLimiterOptions.SettingsSection,
            "Solo.RequestsPerSecond",
            5);

        // The per-second bucket re-based itself instantly: 10 tokens clamp at
        // the new 5-token capacity without any accrual pass.
        perSecond.ProbeTokens(KlinesLane).MainTokens.Should().Be(5, "the count dialect re-bases the standing tokens immediately");

        // The weight-minute manifest doubled from 4 to 8: the emptied user
        // pool refills its own doubled half-flow (8 x 0.5 / 0.1 s = 40/s) at
        // one interval; the wake arms at the 100 ms fit plus the 2 ms
        // overshoot, so the advance only crosses it a little past the fit —
        // 110 ms accrues 4.4 of the weight-four ask.
        var refilled = weightMinute.AcquireAsync(RestLane.User, KlinesLane, 4);
        clock.Advance(TimeSpan.FromMilliseconds(110));
        await refilled.WaitAsync(TimeSpan.FromMilliseconds(200));
        refilled.IsCompletedSuccessfully.Should().BeTrue("the doubled budget's flow refilled the pool within one interval");

        weightMinute.ProbeTokens(KlinesLane).MainTokens.Should()
            .BeInRange(0.3, 0.5, "the spend took the weight-four ask out of the 4.4 accrued");
    }

    /// <summary>
    /// A non-positive budget key is ignored with a logged warning — the
    /// shipped buckets keep standing, never silently zeroed.
    /// </summary>
    [Fact]
    public async Task SettingsChange_NonPositiveBudgetKey_KeepsTheShippedBucketsStanding()
    {
        var clock = new ManualTimeProvider();
        using var settings = new TestSettingsService();
        using var limiter = new RestRateLimiter(
            SingleBucket(KlinesLane, 4, TimeSpan.FromMilliseconds(100)),
            settings: settings,
            timeProvider: clock);

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, "Test.RequestWeightMinute", 0);

        await limiter.AcquireAsync(RestLane.User, KlinesLane, 4);
        limiter.ProbeTokens(KlinesLane).MainTokens.Should()
            .BeLessThanOrEqualTo(0.01, "the ignored key leaves the shipped four-token bucket standing");
    }

    #endregion
}
