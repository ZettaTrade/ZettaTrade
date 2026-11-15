namespace ZettaTrade.Backtest;

using System.Globalization;
using System.Text;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// How a Monte Carlo sampling ended: with the full distribution folded, or
/// cancelled cooperatively between paths. A cancelled sampling carries no
/// distribution and no numbers — the stop is the outcome, never a partial
/// answer.
/// </summary>
public enum BacktestMonteCarloOutcome
{
    /// <summary>Every path sampled and the percentiles folded.</summary>
    Completed = 0,

    /// <summary>The stop landed between paths; no distribution was produced.</summary>
    Cancelled = 1,
}

/// <summary>
/// One percentile bundle of the sampling (T-254): the five honest points of
/// the distribution the paths produced. The percentile method is the
/// deterministic nearest-rank (the k-th smallest, k = ceil(p/100 · N)) — the
/// same paths always fold into the same five numbers.
/// </summary>
/// <param name="P5">The 5th percentile (the pessimistic tail).</param>
/// <param name="P25">The 25th percentile.</param>
/// <param name="Median">The 50th percentile.</param>
/// <param name="P75">The 75th percentile.</param>
/// <param name="P95">The 95th percentile (the optimistic tail).</param>
public sealed record BacktestMonteCarloPercentiles(
    decimal P5,
    decimal P25,
    decimal Median,
    decimal P75,
    decimal P95);

/// <summary>
/// The deterministic input of one Monte Carlo sampling (T-254): a completed
/// run's report (the trade sequence the sampling reshuffles comes from its
/// own fills), the path count and the sampling's seed. The sampling is
/// bootstrap: every path redraws the whole trade sequence WITH replacement
/// — the order and the multiplicity reshuffle, the trade set itself stays
/// the report's own.
/// </summary>
/// <remarks>
/// The seed is the sampling's own knob, independent from the replay's: the
/// same report over the same seed folds the same distribution byte-stably.
/// Everything invalid is refused before any path samples — the standing
/// refuse-not-default rule; a report with no closed trades has no sequence
/// to resample and is refused, never fabricated into a distribution.
/// </remarks>
public sealed record BacktestMonteCarloRequest
{
    #region Constants

    /// <summary>The path count a sampling carries when the caller pins none:
    /// the acceptance's own shape — ten thousand paths fold in seconds.</summary>
    public const int DefaultPaths = 10_000;

    /// <summary>The smallest path count a sampling accepts: percentiles over
    /// fewer paths are noise, not a distribution.</summary>
    public const int MinPaths = 100;

    /// <summary>The largest path count a sampling accepts: the sanity ceiling
    /// a settings typo must not pin an unbounded fold.</summary>
    public const int MaxPaths = 100_000;

    /// <summary>The largest total draw budget (paths × trades) a sampling
    /// accepts: a pathological report (a huge window's trade sequence) with
    /// the maximum path count would fold for minutes — the budget refuses
    /// the combination up front with its own named counts.</summary>
    public const long MaxTotalDraws = 20_000_000;

    /// <summary>The seed a sampling carries when the caller pins none.</summary>
    public const int DefaultSeed = 0;

    #endregion Constants

    #region Required Identity

    /// <summary>The completed run's report the trade sequence comes from (the fills book the sequence, the statistics name its size).</summary>
    public required BacktestReport Report { get; init; }

    #endregion Required Identity

    #region Sampling Knobs

    /// <summary>How many equity paths the sampling folds.</summary>
    public int Paths { get; init; } = DefaultPaths;

    /// <summary>The seed of the sampling's deterministic draws (the reshuffle's only stochastic input).</summary>
    public int Seed { get; init; } = DefaultSeed;

    #endregion Sampling Knobs

    #region Validation

    /// <summary>
    /// Refuse an invalid sampling before any path draws: the report's own
    /// fill sequence extracts (a malformed fill is refused there), the
    /// sequence must be non-empty and agree with the statistics' own trade
    /// count, and the work budget (paths × trades) must hold. Nothing here
    /// is ever defaulted, corrected or clamped silently — the standing rule.
    /// </summary>
    /// <exception cref="ArgumentNullException">The report is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The path count leaves <see cref="MinPaths"/>..<see cref="MaxPaths"/>, or the work budget is exceeded.</exception>
    /// <exception cref="InvalidOperationException">The report closed no trades, or its statistics disagree with its own fills.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Report);

        if (Paths < MinPaths || Paths > MaxPaths)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Paths),
                Paths,
                $"The paths count must be between {MinPaths} and {MaxPaths}.");
        }

        var trades = BacktestMonteCarlo.ExtractTradePnLs(Report.Fills);

        if (trades.Count == 0)
        {
            throw new InvalidOperationException(
                "The report closed no round trips; there is no trade sequence to resample and the Monte Carlo is refused, never fabricated.");
        }

        if (trades.Count != Report.Statistics.Trades)
        {
            throw new InvalidOperationException(
                $"The report's statistics disagree with its own fill sequence (statistics name {Report.Statistics.Trades} closed trade(s), the fills book {trades.Count}); the report is malformed and the sampling is refused, never guessed.");
        }

        if ((long)Paths * trades.Count > MaxTotalDraws)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Paths),
                Paths,
                $"The sampling draws {Paths} path(s) over {trades.Count} trade(s) — {MaxTotalDraws} draws is the budget; lower the path count or replay a shorter window.");
        }
    }

    #endregion Validation
}

/// <summary>
/// The folded distribution of one Monte Carlo sampling (T-254): the
/// percentiles of the paths' final equity and of their max drawdown, plus
/// how many paths ended below the starting balance. Every number folds from
/// the seeded paths — the same report over the same seed is byte-identical.
/// </summary>
/// <param name="Request">The sampling's request (the report echo, the paths count and the seed).</param>
/// <param name="Trades">The closed trades the paths resample (the report's own sequence length).</param>
/// <param name="FinalEquity">The percentiles of the paths' final equity.</param>
/// <param name="MaxDrawdown">The percentiles of the paths' max drawdown.</param>
/// <param name="PathsBelowStart">How many paths ended strictly below the starting balance.</param>
public sealed record BacktestMonteCarloDistribution(
    BacktestMonteCarloRequest Request,
    int Trades,
    BacktestMonteCarloPercentiles FinalEquity,
    BacktestMonteCarloPercentiles MaxDrawdown,
    int PathsBelowStart)
{
    #region Facts

    /// <summary>The folded path count.</summary>
    public int Paths => Request.Paths;

    /// <summary>The sampling's seed.</summary>
    public int Seed => Request.Seed;

    /// <summary>The starting balance every path begins from (the report's own starting quote).</summary>
    public decimal InitialEquity => Request.Report.Request.InitialQuoteAmount;

    #endregion Facts

    #region Canonical Serialization

    /// <summary>
    /// The canonical byte-stable serialization of the distribution: the
    /// report echo, the sampling's knobs and the two percentile bundles,
    /// invariant everywhere. The same report over the same seed produces
    /// byte-identical text — the sampling's determinism acceptance reads
    /// exactly this text against the previous one.
    /// </summary>
    public string ToCanonicalText()
    {
        var text = new StringBuilder();
        var report = Request.Report;
        var request = report.Request;

        text.Append("BacktestMonteCarloReport v1").AppendLine();
        text.Append("StrategyId=").Append(request.StrategyId).AppendLine();
        text.Append("AdapterId=").Append(request.AdapterId).AppendLine();
        text.Append("Symbol=").Append(request.Symbol.Normalized).AppendLine();
        text.Append("Interval=").Append(request.Interval.ToString()).AppendLine();
        text.Append("Bars=").Append(request.Bars.ToString(CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Paths=").Append(Request.Paths.ToString(CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Seed=").Append(Request.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Trades=").Append(Trades.ToString(CultureInfo.InvariantCulture)).AppendLine();
        text.Append("InitialEquity=").Append(Format(InitialEquity)).AppendLine();
        AppendPercentiles(text, "FinalEquity", FinalEquity);
        AppendPercentiles(text, "MaxDrawdown", MaxDrawdown);
        text.Append("PathsBelowStart=").Append(PathsBelowStart.ToString(CultureInfo.InvariantCulture)).AppendLine();

        return text.ToString();
    }

    private static void AppendPercentiles(StringBuilder text, string name, BacktestMonteCarloPercentiles percentiles)
    {
        text.Append(name).Append(" p5=").Append(Format(percentiles.P5));
        text.Append(" p25=").Append(Format(percentiles.P25));
        text.Append(" median=").Append(Format(percentiles.Median));
        text.Append(" p75=").Append(Format(percentiles.P75));
        text.Append(" p95=").Append(Format(percentiles.P95));
        text.AppendLine();
    }

    /// <summary>Format one decimal invariantly (round-trip stable per run).</summary>
    private static string Format(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    #endregion Canonical Serialization
}

/// <summary>
/// The result of one Monte Carlo sampling: the outcome plus the folded
/// distribution (present only when the sampling completed). The record
/// carries a reference, not a copy: a completed distribution is immutable.
/// </summary>
/// <param name="Outcome">How the sampling ended.</param>
/// <param name="Distribution">The folded distribution, or null on a cancelled sampling.</param>
public sealed record BacktestMonteCarloResult(
    BacktestMonteCarloOutcome Outcome,
    BacktestMonteCarloDistribution? Distribution)
{
    /// <summary>The cancelled-sampling envelope: no distribution, no numbers.</summary>
    public static BacktestMonteCarloResult Cancelled { get; } =
        new(BacktestMonteCarloOutcome.Cancelled, Distribution: null);

    /// <summary>True when the sampling completed and carries its distribution.</summary>
    public bool IsCompleted => Outcome == BacktestMonteCarloOutcome.Completed;
}

/// <summary>
/// The pure Monte Carlo compute over a completed backtest report (T-254):
/// the report's closed trades extract from its own fill sequence (the exact
/// average-price bookkeeping the ledger booked them with), then every path
/// bootstrap-redraws the whole sequence with replacement through one seeded
/// generator, folding the path's final equity and max drawdown from the
/// report's starting balance. Entirely pure: no cache, no clock, no event —
/// the same report over the same seed folds the same distribution.
/// </summary>
public static class BacktestMonteCarlo
{
    /// <summary>
    /// Extract the closed trades' realized PnLs from a report's fill
    /// sequence: the ledger's exact average-price bookkeeping replayed over
    /// the journal fills — a reducing share realizes
    /// <c>(price − averageEntry) × closedQty</c> signed by the held side, an
    /// extension re-averages the whole new size, a flip's remainder stamps
    /// fresh at its own fill. The extraction is the sampling's honesty
    /// anchor: the resampled sequence is exactly what the replay booked.
    /// </summary>
    /// <exception cref="ArgumentNullException">The fill list or a fill is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A fill's side is not Buy/Sell, or its execution price is non-positive, or it executed nothing.</exception>
    public static IReadOnlyList<decimal> ExtractTradePnLs(IReadOnlyList<Order> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var pnls = new List<decimal>();
        var signedQuantity = 0m;
        var averageEntry = 0m;

        foreach (var fill in fills)
        {
            ArgumentNullException.ThrowIfNull(fill);

            if (fill.Side is not (OrderSide.Buy or OrderSide.Sell))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fills),
                    fill.Side,
                    "The fill side must be Buy or Sell.");
            }

            var quantity = fill.FilledQuantity.Value;
            if (quantity <= 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fills),
                    quantity,
                    "The fill executed nothing; the trade sequence cannot be extracted.");
            }

            var price = fill.Price.Value;
            if (price <= 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fills),
                    price,
                    "The fill price must be positive.");
            }

            var signedDelta = fill.Side == OrderSide.Buy ? quantity : -quantity;

            if (signedQuantity == 0m)
            {
                // Open from flat: the fresh position stamps at its own fill.
                signedQuantity = signedDelta;
                averageEntry = price;
                continue;
            }

            if (decimal.Sign(signedDelta) == decimal.Sign(signedQuantity))
            {
                // Extend the open side: weighted-average entry over the whole new size.
                var heldAbs = Math.Abs(signedQuantity);
                averageEntry = ((averageEntry * heldAbs) + (price * quantity)) / (heldAbs + quantity);
                signedQuantity += signedDelta;
                continue;
            }

            // Close into (or beyond) the position: the closing share realizes
            // against the current average entry; the remainder keeps the
            // average on a partial close and stamps fresh on a flip.
            var heldSign = decimal.Sign(signedQuantity);
            var held = Math.Abs(signedQuantity);
            var closed = Math.Min(quantity, held);
            pnls.Add((price - averageEntry) * closed * heldSign);

            var newSigned = signedQuantity + signedDelta;
            if (newSigned == 0m)
            {
                signedQuantity = 0m;
                averageEntry = 0m;
            }
            else if (decimal.Sign(newSigned) == decimal.Sign(signedQuantity))
            {
                signedQuantity = newSigned;
            }
            else
            {
                signedQuantity = newSigned;
                averageEntry = price;
            }
        }

        return pnls;
    }

    /// <summary>
    /// Fold the distribution: every path bootstrap-redraws
    /// <c>Trades.Count</c> trades with replacement through the seeded
    /// generator (the xoshiro256** explicit-seed <see cref="Random"/>), walks
    /// the equity from the report's starting balance and folds its max
    /// drawdown; the percentiles fold nearest-rank over the sorted paths.
    /// The stop is cooperative between paths: a cancelled fold throws the
    /// cancellation (the caller owns the cancelled envelope), nothing
    /// half-folded is ever answered.
    /// </summary>
    /// <exception cref="ArgumentNullException">The request is missing.</exception>
    /// <exception cref="OperationCanceledException">The stop token fired between paths.</exception>
    public static BacktestMonteCarloDistribution Compute(
        BacktestMonteCarloRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var trades = ExtractTradePnLs(request.Report.Fills);
        var start = request.Report.Request.InitialQuoteAmount;
        var tradeCount = trades.Count;

        // Deterministic seed: the sampling's only stochastic input (the
        // reshuffle draw) derives from the explicit seed, stable across runs
        // of one runtime — the replay's own discipline.
        var prng = new Random(request.Seed);

        var finals = new decimal[request.Paths];
        var drawdowns = new decimal[request.Paths];
        var belowStart = 0;

        for (var path = 0; path < request.Paths; path++)
        {
            // The cooperative stop, checked at a fixed stride: the fold stays
            // responsive and a cancelled sampling answers nothing.
            if ((path & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var equity = start;
            var peak = start;
            var maxDrawdown = 0m;

            for (var trade = 0; trade < tradeCount; trade++)
            {
                equity += trades[prng.Next(tradeCount)];
                if (equity > peak)
                {
                    peak = equity;
                }

                var drawdown = peak - equity;
                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                }
            }

            finals[path] = equity;
            drawdowns[path] = maxDrawdown;
            if (equity < start)
            {
                belowStart++;
            }
        }

        // Nearest-rank percentiles over the sorted paths: deterministic.
        Array.Sort(finals);
        Array.Sort(drawdowns);

        return new BacktestMonteCarloDistribution(
            Request: request,
            Trades: tradeCount,
            FinalEquity: Percentiles(finals),
            MaxDrawdown: Percentiles(drawdowns),
            PathsBelowStart: belowStart);
    }

    /// <summary>Fold one percentile bundle nearest-rank over the sorted values.</summary>
    private static BacktestMonteCarloPercentiles Percentiles(decimal[] sorted) => new(
        P5: PercentileAt(sorted, 5),
        P25: PercentileAt(sorted, 25),
        Median: PercentileAt(sorted, 50),
        P75: PercentileAt(sorted, 75),
        P95: PercentileAt(sorted, 95));

    /// <summary>The deterministic nearest-rank percentile: the k-th smallest
    /// value (1-based, k = ceil(p/100 · N), clamped into 1..N).</summary>
    private static decimal PercentileAt(decimal[] sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100m * sorted.Length);
        if (rank < 1)
        {
            rank = 1;
        }

        if (rank > sorted.Length)
        {
            rank = sorted.Length;
        }

        return sorted[rank - 1];
    }
}
