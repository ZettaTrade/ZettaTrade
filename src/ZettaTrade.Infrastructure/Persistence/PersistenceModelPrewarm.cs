namespace ZettaTrade.Infrastructure.Persistence;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The startup model pre-warm (T-237): every SQLite context builds its EF
/// model lazily on the first real operation, and those builds (plus the
/// one-time EF Core internals warm-up the first build pays) serialized a
/// second of the store batch's wall inside the composition walk — the
/// largest single block of the cold start after the window build itself.
/// This helper kicks the same builds EARLY, off the walk: worker-thread
/// tasks racing the UI bootstrap, so the walk's own store batch finds the
/// models already built and its wall collapses to the per-store migration
/// checks.
///
/// A model is a per-process cache keyed by the context type, and the
/// connection string is not part of it, so the pre-warm contexts ride
/// throwaway in-memory options: they never open a connection, never touch
/// a database file and never observe real data. A build that fails or
/// finishes late degrades harmlessly — the walk's own first touch builds
/// the model exactly as before, so correctness never depends on this
/// helper at all.
/// </summary>
public static class PersistenceModelPrewarm
{
    /// <summary>
    /// The persistence layer's context types: the five walked stores plus
    /// the PnL ledger's context (its model build rides the PnL
    /// accounting's walk step). One throwaway build each.
    /// </summary>
    private static readonly Func<DbContext>[] Builders =
    [
        () => new CandleCacheDbContext(ThrowawayOptions<CandleCacheDbContext>()),
        () => new OrderHistoryDbContext(ThrowawayOptions<OrderHistoryDbContext>()),
        () => new PnlDbContext(ThrowawayOptions<PnlDbContext>()),
        () => new ChatHistoryDbContext(ThrowawayOptions<ChatHistoryDbContext>()),
        () => new PortfolioValueDbContext(ThrowawayOptions<PortfolioValueDbContext>()),
        () => new ChartDrawingDbContext(ThrowawayOptions<ChartDrawingDbContext>()),
    ];

    private static int _started;

    /// <summary>The kicked warm-up task (the T-231 pump canon: a
    /// fire-and-forget rides a field, never a naked discard). The task
    /// never faults — its per-build exceptions are caught inside, and the
    /// settled outcome answers through <see cref="Outcome"/>.</summary>
    private static Task? _warmAll;

    private static readonly TaskCompletionSource<PrewarmOutcome> Settled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether this process has kicked the builds: the receipt's
    /// consumers gate on it, so a process that never started the warm-up
    /// (a test host) never holds a pending outcome task.</summary>
    public static bool Started => Volatile.Read(ref _started) == 1;

    /// <summary>
    /// Kick the model builds once per process. Safe to call again: later
    /// calls are no-ops. Call it as early as the process can afford — the
    /// builds then race whatever the main thread does next (the UI
    /// bootstrap) instead of serializing inside the composition walk.
    /// </summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        _warmAll = WarmAllAsync();
    }

    /// <summary>
    /// The settled outcome, for the composition's one receipt line and the
    /// tests: completes once every build has settled (warm or degraded).
    /// Never faults — the callers observe it freely.
    /// </summary>
    public static Task<PrewarmOutcome> Outcome => Settled.Task;

    /// <summary>How many builds landed warm, how many degraded, the wall.</summary>
    public sealed record PrewarmOutcome(int Warmed, int Failed, double WallMs);

    private static async Task WarmAllAsync()
    {
        var wall = Stopwatch.StartNew();
        var warmed = 0;
        var failed = 0;

        var builds = Builders
            .Select(async build =>
            {
                try
                {
                    await using var dbContext = build();
                    _ = dbContext.Model;
                    Interlocked.Increment(ref warmed);
                }
                catch (Exception)
                {
                    // A degraded pre-warm is the honest state: the walk's
                    // own first touch builds the model exactly as before,
                    // so the failure is invisible to correctness.
                    Interlocked.Increment(ref failed);
                }
            })
            .ToArray();

        await Task.WhenAll(builds);

        Settled.TrySetResult(new PrewarmOutcome(warmed, failed, wall.Elapsed.TotalMilliseconds));
    }

    private static DbContextOptions<TContext> ThrowawayOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
}
