namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The explicit reconnect timing policy (T-105): exponential backoff from the
/// initial delay, bounded jitter around every computed wait and a hard
/// max-delay cap — the previously implicit single fixed delay of
/// <see cref="WebSocketClient"/>, made a testable value object. An adapter
/// carries its own instance (see <see cref="BinanceAdapter.DefaultReconnectPolicy"/>
/// and <see cref="BybitAdapter.DefaultReconnectPolicy"/>); the supervisor
/// exposes it alongside the per-adapter state so the future health strip
/// (T-098) shows the timing an adapter actually runs.
/// </summary>
public sealed record ReconnectPolicy
{
    /// <summary>Upper step count of the exponential growth loop; covers even a degenerate multiplier of exactly 1.0.</summary>
    private const int MaxBackoffSteps = 64;

    /// <summary>First-retry wait of the shipped defaults, in seconds.</summary>
    private const int DefaultInitialDelaySeconds = 5;

    /// <summary>Max-delay cap of the shipped defaults, in seconds.</summary>
    private const int DefaultMaxDelaySeconds = 60;

    /// <summary>Growth factor of the shipped defaults (a doubling backoff).</summary>
    private const double DefaultBackoffMultiplier = 2.0;

    /// <summary>Jitter fraction of the shipped defaults (±25 % around the base wait).</summary>
    private const double DefaultJitterFraction = 0.25;

    /// <summary>Initial-connect attempt budget of the shipped defaults.</summary>
    private const int DefaultMaxAttempts = 5;

    #region Properties

    /// <summary>
    /// The wait before the first retry. The first failure path keeps the
    /// previously fixed five-second spacing, so an existing reconnect burst
    /// behaves identically on its first retry.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive value.</exception>
    public TimeSpan InitialDelay
    {
        get;
        init => field = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(InitialDelay), value, "The initial reconnect delay must be positive.");
    }

    /// <summary>
    /// The hard cap on every computed wait — the backoff never waits longer,
    /// and the jitter can never push a wait past it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Smaller than the initial delay.</exception>
    public TimeSpan MaxDelay
    {
        get;
        init => field = value >= InitialDelay
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxDelay),
                value,
                "The max reconnect delay must be at least the initial delay.");
    }

    /// <summary>
    /// The growth factor per failed attempt (1.0 keeps a constant spacing, 2.0
    /// doubles every retry).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 1.0.</exception>
    public double BackoffMultiplier
    {
        get;
        init => field = value >= 1.0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(BackoffMultiplier),
                value,
                "The backoff multiplier must be at least 1.0.");
    }

    /// <summary>
    /// The fractional jitter applied around the base delay: a wait lands
    /// within [base·(1−j), base·(1+j)], clamped to the max delay. Zero
    /// switches the jitter off entirely (fully deterministic waits).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside [0, 1).</exception>
    public double JitterFraction
    {
        get;
        init => field = value is >= 0.0 and < 1.0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(JitterFraction),
                value,
                "The jitter fraction must be within [0.0, 1.0).");
    }

    /// <summary>
    /// The failed-handshake attempt budget of one initial connect call —
    /// the per-session budget a fresh <see cref="WebSocketClient.ConnectAsync"/>
    /// resets to (BUG-029); the background reconnect loop stays unbounded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive value.</exception>
    public int MaxAttempts
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxAttempts), value, "The reconnect attempt budget must be positive.");
    }

    #endregion

    #region Static defaults

    /// <summary>
    /// The shipped production timing: the T-052 five-second single delay as
    /// the first retry, doubling backoff after further failures, ±25 % jitter
    /// against thundering-herd reconnects and a one-minute cap. The shipped
    /// per-adapter policies are separate instances of exactly this shape, so
    /// they can diverge per adapter without touching this record.
    /// </summary>
    public static ReconnectPolicy Default { get; } = new()
    {
        InitialDelay = TimeSpan.FromSeconds(DefaultInitialDelaySeconds),
        MaxDelay = TimeSpan.FromSeconds(DefaultMaxDelaySeconds),
        BackoffMultiplier = DefaultBackoffMultiplier,
        JitterFraction = DefaultJitterFraction,
        MaxAttempts = DefaultMaxAttempts,
    };

    #endregion

    #region Methods

    /// <summary>
    /// Compute the wait after the <paramref name="attempt"/>-th consecutive
    /// failure (1-based): base = <see cref="InitialDelay"/>·<see cref="BackoffMultiplier"/>^(attempt−1),
    /// capped at <see cref="MaxDelay"/>, then jittered by
    /// <paramref name="jitterSample"/> across the bounded jitter range and
    /// re-clamped, so every returned wait sits within
    /// [base·(1−j), min(base·(1+j), <see cref="MaxDelay"/>)]. Pure and
    /// deterministic for a fixed sample — the randomness comes solely from
    /// the caller's sample draw (<see cref="Random.Shared"/>).
    /// </summary>
    /// <param name="attempt">1-based consecutive-failure position.</param>
    /// <param name="jitterSample">Uniform sample from [0, 1].</param>
    /// <returns>The bounded wait.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Attempt below 1 or the sample outside [0, 1] — a bad input is an error, never a substituted default.
    /// </exception>
    public TimeSpan ComputeDelay(int attempt, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (jitterSample is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterSample),
                jitterSample,
                "The jitter sample must be within [0.0, 1.0].");
        }

        var capMilliseconds = MaxDelay.TotalMilliseconds;
        var delayMilliseconds = InitialDelay.TotalMilliseconds;

        // Iterative growth with a bounded step count: Math.Pow would overflow
        // into infinity for a huge attempt number (Infinity → TimeSpan throws),
        // and a degenerate multiplier of exactly 1.0 never grows past the cap.
        var backoffSteps = Math.Min(attempt - 1, MaxBackoffSteps);
        for (var step = 0; step < backoffSteps && delayMilliseconds < capMilliseconds; step++)
        {
            delayMilliseconds *= BackoffMultiplier;
        }

        if (delayMilliseconds > capMilliseconds)
        {
            delayMilliseconds = capMilliseconds;
        }

        delayMilliseconds *= 1.0 - JitterFraction + (2.0 * JitterFraction * jitterSample);
        if (delayMilliseconds > capMilliseconds)
        {
            delayMilliseconds = capMilliseconds;
        }

        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    #endregion
}
