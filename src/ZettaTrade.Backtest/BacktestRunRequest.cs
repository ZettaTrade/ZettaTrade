namespace ZettaTrade.Backtest;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The deterministic input of one backtest run (T-113): the strategy, the
/// cached series identity and the execution policy. Everything the replay
/// needs is pinned at run start — the run reads the candle cache only:
/// entirely offline, no adapters, no rate limiter, and the backtest can
/// never reach a trading adapter's order surface.
/// </summary>
/// <remarks>
/// Every field is explicit or carries a named default; <see cref="Validate"/>
/// refuses invalid shapes outright (an unknown strategy resolves to an error
/// at run time, never to a substituted default — the standing
/// refuse-not-default rule).
/// </remarks>
public sealed record BacktestRunRequest
{
    #region Constants

    /// <summary>The seed a run carries when the caller pins none. The seed
    /// drives the replay's only stochastic component (the seeded fill
    /// slippage); a zero slippage makes every seed produce the same curve.</summary>
    public const int DefaultSeed = 0;

    /// <summary>The default candle target of one run: a few thousand bars
    /// replays the demo strategy's behavior across several regime changes.
    /// The cache may serve less (the honest coverage) but never more.</summary>
    public const int DefaultBars = 4_096;

    /// <summary>The smallest bar target a run accepts: fewer bars cannot
    /// even warm up a windowed strategy, so a run that tiny answers nothing.</summary>
    public const int MinBars = 32;

    /// <summary>The largest bar target a run accepts: the sanity ceiling a
    /// settings typo ("10^12") must not pin an unbounded load.</summary>
    public const int MaxBars = 100_000;

    /// <summary>The default long-side sizing: the whole equity at the
    /// transition's mark goes into one long stance until the next
    /// stand-aside (the demo strategy's own all-in reading).</summary>
    public const decimal DefaultLongNotionalFraction = 1m;

    /// <summary>The default fill slippage: zero. The paper ledger's fills
    /// execute at the reference close with no added spread; a nonzero value
    /// skews each fill toward the adverse side through the seeded draw.</summary>
    public const decimal DefaultSlippageValue = 0m;

    #endregion Constants

    #region Required Identity

    /// <summary>The canonical strategy id the replay evaluates (the T-111 seam).</summary>
    public required string StrategyId { get; init; }

    /// <summary>The adapter identity keyed into the candle cache — the series
    /// the replay reads and the venue the simulated fills execute on (its
    /// fee schedule, when one exists, stamps the commissions).</summary>
    public required string AdapterId { get; init; }

    /// <summary>The trading symbol of the replayed series.</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>The candle interval of the replayed series.</summary>
    public required TimeFrame Interval { get; init; }

    #endregion Required Identity

    #region Run Knobs

    /// <summary>The seed of the replay's deterministic draws (slippage skew per fill).</summary>
    public int Seed { get; init; } = DefaultSeed;

    /// <summary>The candle target: the newest <see cref="Bars"/> cached bars,
    /// oldest first. Fewer cached bars means the honest shorter replay.</summary>
    public int Bars { get; init; } = DefaultBars;

    /// <summary>The simulated starting quote balance (paper parity).</summary>
    public decimal InitialQuoteAmount { get; init; } = Infrastructure.Exchange.PaperTradingAdapter.InitialQuoteAmount;

    /// <summary>
    /// The long-side sizing: the fraction of the current equity the long
    /// stance converts into notional at each stance change. The exit side
    /// always liquidates the whole position regardless of this fraction.
    /// </summary>
    public decimal LongNotionalFraction { get; init; } = DefaultLongNotionalFraction;

    /// <summary>
    /// The slippage magnitude in quote units: an entry pays up to this much
    /// above (a buy) or below (a sell) the reference close, sketched by the
    /// seeded draw. Zero reproduces the paper ledger's exact reference fills.
    /// </summary>
    public decimal SlippageValue { get; init; } = DefaultSlippageValue;

    #endregion Run Knobs

    #region Validation

    /// <summary>
    /// Refuse an invalid shape before any run starts: nothing here is ever
    /// defaulted, corrected or clamped silently — the standing rule.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required identity is missing.</exception>
    /// <exception cref="ArgumentException">A required identity is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is violated.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Symbol);

        ArgumentException.ThrowIfNullOrWhiteSpace(StrategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);

        if (Bars < MinBars || Bars > MaxBars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Bars),
                Bars,
                $"The bar target must be between {MinBars} and {MaxBars}.");
        }

        if (InitialQuoteAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialQuoteAmount),
                InitialQuoteAmount,
                "The initial quote balance must be positive.");
        }

        if (LongNotionalFraction <= 0m || LongNotionalFraction > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LongNotionalFraction),
                LongNotionalFraction,
                "The long notional fraction must lie in (0, 1].");
        }

        if (SlippageValue < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SlippageValue),
                SlippageValue,
                "The slippage magnitude must be non-negative.");
        }
    }

    #endregion Validation
}
