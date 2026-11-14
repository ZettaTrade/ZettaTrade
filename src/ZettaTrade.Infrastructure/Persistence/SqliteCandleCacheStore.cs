namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// SQLite <see cref="ICandleCache"/>. One <see cref="CandleCacheDbContext"/>
/// per operation (cheap: they share the singleton options object which only
/// carries the connection string), so a write-through never races the read
/// path on live state. The initialize step creates the data directory,
/// applies migrations and loads the reactive <c>Caching</c> policy.
/// </summary>
public sealed class SqliteCandleCacheStore(
    DbContextOptions<CandleCacheDbContext> options,
    ISettingsService settings,
    ILogger<SqliteCandleCacheStore> logger) : ICandleCache, IAsyncInitializable, IAsyncDisposable
{
    #region Constants

    /// <summary>The settings section the candle-cache policy lives under.</summary>
    public const string SettingsSection = "Caching";

    /// <summary>The settings key switching the cache on or off.</summary>
    public const string EnabledSettingsKey = "Enabled";

    /// <summary>The settings key sizing the per-series eviction cap.</summary>
    public const string MaxBarsPerSeriesSettingsKey = "MaxBarsPerSeries";

    /// <summary>The shipped posture: the cache is on out of the box.</summary>
    public const bool DefaultEnabled = true;

    /// <summary>The shipped eviction cap per series; the 100k-candle chart
    /// target needs a series that deep at minimum. Settings may size more.</summary>
    public const int DefaultMaxBarsPerSeries = 100_000;

    /// <summary>The eviction cap's floor: a cap below one bar means nothing.</summary>
    private const int MinMaxBarsPerSeries = 1;

    /// <summary>The eviction cap's sanity ceiling: a settings typo of the
    /// "10^12" kind must not pin an unbounded trim query.</summary>
    private const int AbsoluteMaxBarsPerSeries = 10_000_000;

    /// <summary>Growth cap of one <see cref="GetLatestAsync"/> answer.</summary>
    private const int MaxLatestBatch = 1_000;

    /// <summary>The query-plan warm-up's benign adapter id (T-237): a real
    /// but never-stored id — the read answers zero rows, the compiled plan
    /// is the exact one the chart's first load reuses.</summary>
    public const string WarmupAdapterId = "warmup";

    /// <summary>The warm-up's benign symbol: a known quote asset so
    /// <see cref="Symbol.Parse"/> accepts it; never stored either.</summary>
    public const string WarmupSymbol = "WARM/USDT";

    /// <summary>
    /// The model converters, compiled once, for the raw upsert statements
    /// (the BUG-043 lesson): the interpolated SQL runs AROUND the EF
    /// value-conversion pipeline, so its values must be pre-converted
    /// through the EXACT converters the entity model keeps on each column —
    /// the stored TEXT stays byte-consistent whether a row was written by
    /// the entity pipeline or by the upsert SQL.
    /// </summary>
    private static readonly Func<DateTimeOffset, string> TimestampText =
        OrderHistoryConversions.Timestamp.ConvertToProviderExpression.Compile();

    /// <summary>See <see cref="TimestampText"/>.</summary>
    private static readonly Func<decimal, string> AmountText =
        OrderHistoryConversions.Amount.ConvertToProviderExpression.Compile();

    #endregion Constants

    #region Private Fields

    /// <summary>The on/off snapshot settings changes refresh; readers never lock.</summary>
    private volatile bool _enabled = DefaultEnabled;

    /// <summary>The per-series eviction cap snapshot settings changes refresh.</summary>
    private volatile int _maxBarsPerSeries = DefaultMaxBarsPerSeries;

    /// <summary>The settings subscription, released at disposal.</summary>
    private IDisposable? _changesSubscription;

    #endregion Private Fields

    #region Properties

    /// <summary>Whether the cache serves reads and rides write-throughs:
    /// the loaded <c>Caching</c> policy snapshot, refreshed by settings
    /// changes — the policy probe, never a write path.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>The loaded per-series eviction cap: the cached policy
    /// snapshot settings changes refresh.</summary>
    public int MaxBarsPerSeries => _maxBarsPerSeries;

    #endregion Properties

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var dbContext = new CandleCacheDbContext(options);
        var file = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The candle cache database has no connection string.");

        // Self-contained startup: the store must not assume someone else
        // created the data directory (e.g. freshly installed on a new machine).
        var directory = Path.GetDirectoryName(
            file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                ? file["Data Source=".Length..]
                : file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await dbContext.Database.MigrateAsync(ct);
        await LoadPolicyAsync(ct);

        // T-237: the first query on this context compiles the EF query
        // plan — Debug-lane hundreds of milliseconds the chart's first
        // load otherwise pays inside its constructor, right before the
        // window shows. Pay it here, inside the store batch's concurrent
        // window, with the store's own read shape: a benign adapter id
        // reads zero rows and warms the exact plan the chart reuses. A
        // degraded warm-up only delays nothing — the chart's own first
        // call then compiles the plan exactly as before.
        if (_enabled)
        {
            var warmup = Stopwatch.StartNew();
            try
            {
                _ = await GetLatestAsync(
                    WarmupAdapterId,
                    Symbol.Parse(WarmupSymbol),
                    TimeFrame.H1,
                    1,
                    ct);
                logger.LogDebug(
                    "Candle cache: the first-query plan warmed in {WarmupMs:F0} ms",
                    warmup.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // The walk's cancellation: the chart's own first call
                // warms the plan as before.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Candle cache: the query-plan warm-up degraded");
            }
        }

        // Settings changes re-read the section: each change becomes one
        // read+swap in the reactive pipeline, strictly serialized by the
        // Switch continuation (never overlapping, never abandoned), so a
        // burst of edits converges on the latest policy.
        _changesSubscription = settings.Changes
            .Where(change => string.Equals(change.Section, SettingsSection, StringComparison.Ordinal))
            .Select(_ => Observable.FromAsync(
                () => LoadPolicyAsync(CancellationToken.None)))
            .Switch()
            .Subscribe(
                _ => { },
                error => logger.LogError(error, "Candle cache: the settings change stream failed"));

        logger.LogInformation(
            "Candle cache ready at {DatabaseFile} (migrations applied, eviction cap {MaxBarsPerSeries} bars per series)",
            file,
            _maxBarsPerSeries);
    }

    /// <summary>Reload the policy snapshot from the settings section.</summary>
    private async Task LoadPolicyAsync(CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingsSection, EnabledSettingsKey, DefaultEnabled);
        var configured = await settings.GetAsync<int>(
            SettingsSection,
            MaxBarsPerSeriesSettingsKey,
            DefaultMaxBarsPerSeries);

        _enabled = enabled;
        _maxBarsPerSeries = Math.Clamp(configured, MinMaxBarsPerSeries, AbsoluteMaxBarsPerSeries);
    }

    /// <summary>The cache owns no running work: the subscription dies at disposal.</summary>
    public ValueTask DisposeAsync()
    {
        _changesSubscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Bar>> GetLatestAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(symbol);

        if (!_enabled)
        {
            return [];
        }

        var take = Math.Clamp(limit, 1, MaxLatestBatch);
        await using var dbContext = new CandleCacheDbContext(options);

        var entries = await dbContext.Candles
            .Where(entry => entry.AdapterId == adapterId
                && entry.Symbol == symbol.Normalized
                && entry.TimeFrame == timeFrame)
            .OrderByDescending(entry => entry.OpenTime)
            .Take(take)
            .ToListAsync(cancellationToken);
        entries.Reverse();
        return entries.Select(ToBar).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Bar>> GetOlderThanAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(symbol);

        if (!_enabled)
        {
            return [];
        }

        var take = Math.Clamp(limit, 1, MaxLatestBatch);
        await using var dbContext = new CandleCacheDbContext(options);

        var entries = await dbContext.Candles
            .Where(entry => entry.AdapterId == adapterId
                && entry.Symbol == symbol.Normalized
                && entry.TimeFrame == timeFrame
                && entry.OpenTime < before)
            .OrderByDescending(entry => entry.OpenTime)
            .Take(take)
            .ToListAsync(cancellationToken);
        entries.Reverse();
        return entries.Select(ToBar).ToArray();
    }

    /// <inheritdoc/>
    public async Task WriteThroughAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        IReadOnlyList<Bar> bars,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0 || !_enabled)
        {
            return;
        }

        await using var dbContext = new CandleCacheDbContext(options);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Every bar is one TRUE DB upsert (the BUG-043 lesson): the
        // check-then-act Find/Add pattern left a window where two concurrent
        // writers both saw null and the second SaveChanges died on the
        // unique constraint. ONE statement decides insert-or-update on the
        // database side — a restart replay and any racing surface merge on
        // the composite key (adapter, symbol, interval, open time) instead
        // of colliding on it, and an overlap conflates, never duplicates.
        //
        // The interpolated SQL runs AROUND the EF value converters, so every
        // value is pre-converted through THE SAME converter the entity model
        // keeps on that column — the stored TEXT stays byte-consistent with
        // what the entity pipeline would have written. The batch commits as
        // one transaction: a served page lands whole or not at all.
        foreach (var bar in bars)
        {
            var openTime = TimestampText(bar.Time);
            var open = AmountText(bar.Open);
            var high = AmountText(bar.High);
            var low = AmountText(bar.Low);
            var close = AmountText(bar.Close);
            var volume = AmountText(bar.Volume);

            _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO Candles (AdapterId, Symbol, TimeFrame, OpenTime, Open, High, Low, Close, Volume)
                VALUES ({adapterId}, {symbol.Normalized}, {timeFrame.ToString()}, {openTime}, {open}, {high}, {low}, {close}, {volume})
                ON CONFLICT (AdapterId, Symbol, TimeFrame, OpenTime) DO UPDATE SET
                    Open = {open},
                    High = {high},
                    Low = {low},
                    Close = {close},
                    Volume = {volume}
                """,
                cancellationToken);
        }

        await EvictOverflowAsync(dbContext, adapterId, symbol.Normalized, timeFrame.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Trim one series back to the eviction cap: only the newest bars stay,
    /// everything older than the cap's window is deleted in the same
    /// transaction that just wrote. The count matters only when it moved —
    /// a steady cache logs nothing.
    /// </summary>
    private async Task EvictOverflowAsync(
        CandleCacheDbContext dbContext,
        string adapterId,
        string symbol,
        string timeFrame,
        CancellationToken cancellationToken)
    {
        var cap = _maxBarsPerSeries;
        var evicted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM Candles
            WHERE AdapterId = {adapterId} AND Symbol = {symbol} AND TimeFrame = {timeFrame}
              AND OpenTime NOT IN (
                  SELECT OpenTime FROM Candles
                  WHERE AdapterId = {adapterId} AND Symbol = {symbol} AND TimeFrame = {timeFrame}
                  ORDER BY OpenTime DESC
                  LIMIT {cap})
            """,
            cancellationToken);

        if (evicted > 0)
        {
            logger.LogInformation(
                "Candle cache evicted {Count} oldest bar(s) for {AdapterId}/{Symbol} {TimeFrame} (cap {MaxBarsPerSeries})",
                evicted,
                adapterId,
                symbol,
                timeFrame,
                cap);
        }
    }

    /// <summary>Map a cached row back onto the Core <see cref="Bar"/> record.</summary>
    private static Bar ToBar(CandleCacheEntry entry) => new(
        entry.OpenTime,
        entry.Open,
        entry.High,
        entry.Low,
        entry.Close,
        entry.Volume);

    #endregion Private Methods
}
