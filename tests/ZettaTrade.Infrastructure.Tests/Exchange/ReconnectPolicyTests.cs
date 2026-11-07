namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The reconnect policy's own contract (T-105): the full timing matrix as a
/// pure function — exponential growth, the max-delay cap, the jitter bounds
/// against their documented interval, the shipped production defaults and the
/// fail-fast validation (a bad input is an error, never a substituted
/// default).
/// </summary>
public sealed class ReconnectPolicyTests
{
    #region Constants

    /// <summary>Compact doubling-backoff policy shape reused across the timing facts.</summary>
    private static readonly TimeSpan SmallInitialDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The cap every test policy shares (also the jitter-bound ceiling reference).</summary>
    private static readonly TimeSpan SmallMaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>Millisecond slack that absorbs double→TimeSpan rounding in the bound assertions.</summary>
    private const double RoundingSlackMilliseconds = 1.0;

    #endregion

    #region Tests: compute matrix

    /// <summary>The jitter-off policy: attempt 1 is exactly the initial delay, further attempts double and the cap holds.</summary>
    [Fact]
    public void ComputeDelay_WithoutJitter_GrowsExponentiallyAndCaps()
    {
        var policy = DeterministicJitterOffPolicy();

        policy.ComputeDelay(1, 0.0).Should().Be(SmallInitialDelay);
        policy.ComputeDelay(2, 0.0).Should().Be(TimeSpan.FromMilliseconds(200));
        policy.ComputeDelay(3, 0.0).Should().Be(TimeSpan.FromMilliseconds(400));
        policy.ComputeDelay(100, 0.0).Should().Be(SmallMaxDelay, "the backoff caps, it never exceeds the max delay");
        policy.ComputeDelay(1_000_000, 0.0).Should().Be(SmallMaxDelay, "even a huge attempt number stays at the cap");
    }

    /// <summary>Every computed wait sits within the documented bounds [base·(1−j), min(base·(1+j), cap)] — the jitter is bounded, never a hairdo for the cap.</summary>
    [Fact]
    public void ComputeDelay_WithJitter_StaysWithinTheDocumentedBounds()
    {
        var policy = ShippedShapePolicy();
        const double jitterFraction = 0.25;
        var samples = new[] { 0.0, 0.25, 0.5, 0.75, 0.999 };

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var baseMilliseconds = Math.Min(5_000 * Math.Pow(2, attempt - 1), 60_000);
            var low = TimeSpan.FromMilliseconds(
                (baseMilliseconds * (1.0 - jitterFraction)) - RoundingSlackMilliseconds);
            var high = TimeSpan.FromMilliseconds(
                Math.Min(baseMilliseconds * (1.0 + jitterFraction), 60_000) + RoundingSlackMilliseconds);

            foreach (var sample in samples)
            {
                var delay = policy.ComputeDelay(attempt, sample);
                delay.Should().BeGreaterThanOrEqualTo(low);
                delay.Should().BeLessThanOrEqualTo(high);
            }
        }
    }

    /// <summary>The cap plus jitter can never push a wait past the max delay (the re-clamp pins the upper bound; the floor never touches zero).</summary>
    [Fact]
    public void ComputeDelay_AtTheCap_KeepsTheWaitUnderTheMaxAndAboveZero()
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = SmallInitialDelay,
            MaxDelay = SmallMaxDelay,
            BackoffMultiplier = 2.0,
            JitterFraction = 0.25,
            MaxAttempts = 5,
        };

        var waits = Enumerable
            .Range(0, 64)
            .Select(index => policy.ComputeDelay(9, index / 63.0))
            .ToList();

        waits.Should().OnlyContain(delay => delay <= SmallMaxDelay);
        waits.Should().OnlyContain(delay => delay > TimeSpan.Zero);
    }

    /// <summary>A deterministic (jitter-off) policy ignores its sample entirely.</summary>
    [Fact]
    public void ComputeDelay_WithoutJitter_IsIndependentOfTheSample()
    {
        var policy = DeterministicJitterOffPolicy();

        policy.ComputeDelay(3, 0.0).Should().Be(policy.ComputeDelay(3, 0.999));
        policy.ComputeDelay(3, 0.5).Should().Be(policy.ComputeDelay(3, 0.999));
    }

    #endregion

    #region Tests: shipped defaults

    /// <summary>
    /// The shipped production timing: the T-052 five-second first retry,
    /// doubling backoff, ±25 % jitter, the one-minute cap and the
    /// five-attempt initial-connect budget.
    /// </summary>
    [Fact]
    public void Default_CarriesTheShippedProductionTiming()
    {
        var policy = ReconnectPolicy.Default;

        policy.InitialDelay.Should().Be(TimeSpan.FromSeconds(5));
        policy.MaxDelay.Should().Be(TimeSpan.FromSeconds(60));
        policy.BackoffMultiplier.Should().Be(2.0);
        policy.JitterFraction.Should().Be(0.25);
        policy.MaxAttempts.Should().Be(5);

        // The mid-jitter first wait stays at five seconds: the first reconnect
        // keeps exactly where the T-052 fixed delay was (zero drift).
        policy.ComputeDelay(1, 0.5).Should().Be(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The shipped per-adapter policies are explicit objects carrying the same
    /// shipped shape (an adapter may diverge later; today they match and stay
    /// separate instances).
    /// </summary>
    [Fact]
    public void ShippedAdapterPolicies_MatchTheShippedDefaults()
    {
        BinanceAdapter.DefaultReconnectPolicy.Should().Be(ReconnectPolicy.Default);
        BybitAdapter.DefaultReconnectPolicy.Should().Be(ReconnectPolicy.Default);
        BinanceAdapter.DefaultReconnectPolicy.Should().NotBeSameAs(ReconnectPolicy.Default);
        BybitAdapter.DefaultReconnectPolicy.Should().NotBeSameAs(ReconnectPolicy.Default);
    }

    #endregion

    #region Tests: fail-fast construction

    /// <summary>Invalid construction input is an argument error, never a substituted default.</summary>
    [Theory]
    [InlineData(0.0, 2.0, 0.25, 5)]     // non-positive initial delay
    [InlineData(200.0, 0.5, 0.25, 5)]   // multiplier below 1.0
    [InlineData(200.0, 2.0, -0.1, 5)]   // jitter below [0, 1)
    [InlineData(200.0, 2.0, 1.0, 5)]    // jitter at the [0, 1) boundary
    [InlineData(200.0, 2.0, 0.25, 0)]   // non-positive attempt budget
    public void Ctor_InvalidMember_Throws(double initialDelayMs, double multiplier, double jitter, int attempts)
    {
        var build = () => new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(initialDelayMs),
            MaxDelay = SmallMaxDelay,
            BackoffMultiplier = multiplier,
            JitterFraction = jitter,
            MaxAttempts = attempts,
        };

        build.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>A max delay below the initial delay is a construction error.</summary>
    [Fact]
    public void Ctor_MaxDelayBelowInitialDelay_Throws()
    {
        var build = () => new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(5),
            BackoffMultiplier = 2.0,
            JitterFraction = 0.25,
            MaxAttempts = 5,
        };

        build.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>ComputeDelay validates its attempt position (the 1-based contract).</summary>
    [Fact]
    public void ComputeDelay_NonPositiveAttempt_Throws()
    {
        var policy = DeterministicJitterOffPolicy();

        var compute = () => policy.ComputeDelay(0, 0.0);

        compute.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>ComputeDelay validates the jitter sample ([0, 1] — outside is an error, never a clipped default).</summary>
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void ComputeDelay_SampleOutsideTheUnitInterval_Throws(double sample)
    {
        var policy = DeterministicJitterOffPolicy();

        var compute = () => policy.ComputeDelay(1, sample);

        compute.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Helpers

    /// <summary>The fast deterministic policy: 100 ms initial doubling to a 10 s cap with the jitter off.</summary>
    private static ReconnectPolicy DeterministicJitterOffPolicy() => new()
    {
        InitialDelay = SmallInitialDelay,
        MaxDelay = SmallMaxDelay,
        BackoffMultiplier = 2.0,
        JitterFraction = 0.0,
        MaxAttempts = 5,
    };

    /// <summary>The shipped-shape policy (5 s → 60 s, ×2, ±25 % jitter) reused for the bound fact.</summary>
    private static ReconnectPolicy ShippedShapePolicy() => ReconnectPolicy.Default;

    #endregion
}
