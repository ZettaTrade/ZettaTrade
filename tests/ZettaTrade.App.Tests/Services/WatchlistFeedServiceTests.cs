namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Demo;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The watchlist feed facts (T-120 over the pulse seam, the row set
/// reworked in T-196 over a real catalog): the quotes hold until the first
/// pulse beats (which always ships the row set, unpriced rows carrying
/// null fields), a tick followed by a pulse lands the row's values, the
/// row's price ring caps at its capacity keeping the newest prices, and a
/// selection join retires the previous selection-only row once it leaves.
/// The catalog-driven re-arms: a mutation's publish re-arms the row set in
/// the list's stored order — an added symbol appends, a move reorders with
/// the tick state surviving, a switch swaps the sets, and the selection's
/// overlay re-pins across the switch while list rows never retire from
/// selection moves.
/// <para>
/// The ticker retry ladder (T-231): a stream failure logs ONCE at warning,
/// the pump re-arms on the bounded backoff (a revived stream re-prices the
/// row and keeps its sparkline history), the ladder's own churn stays
/// debug-level, and a retired or disposed pump never resurrects.
/// </para>
/// </summary>
public sealed class WatchlistFeedServiceTests
{
    /// <summary>The starter row order the panel claims (mirrored, not read).</summary>
    private static readonly string[] StarterTexts =
    [
        "BTC/USDT",
        "ETH/USDT",
        "SOL/USDT",
        "XRP/USDT",
        "ADA/USDT",
        "DOGE/USDT",
        "BNB/USDT",
        "LINK/USDT",
        "AVAX/USDT",
        "LTC/USDT",
    ];

    private const decimal RingCapacity = 32;

    /// <summary>
    /// The deterministic fast ladder for the retry facts: 10 ms doubling
    /// to a 20 ms cap, no jitter.
    /// </summary>
    private static readonly ReconnectPolicy TinyRetryPolicy = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(20),
        BackoffMultiplier = 2.0,
        JitterFraction = 0.0,
    };

    #region Fixtures

    /// <summary>
    /// One feed over a real catalog (a real store in a fresh temp
    /// directory — the seeded read completes before the feed arms),
    /// a substituted adapter, subject streams and the controllable pulse.
    /// The optional retry knobs serve the T-231 facts: a retry policy seam
    /// injects the deterministic ladder, the fresh-lane mode replaces the
    /// lane per SubscribeTicker call (a revived stream re-runs the start),
    /// and the recording logger keeps the failure families assertable.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(bool freshLanePerSubscribe = false, ReconnectPolicy? retryPolicy = null)
        {
            FreshLanePerSubscribe = freshLanePerSubscribe;
            Directory = Path.Combine(Path.GetTempPath(), "t196-feed-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Settings = new SettingsService(
                Path.Combine(Directory, "settings.json"),
                NullLogger<SettingsService>.Instance);
            Catalog = new WatchlistCatalogService(
                Settings,
                NullLogger<WatchlistCatalogService>.Instance);
            Feeds = new ConcurrentDictionary<string, Subject<Ticker>>(StringComparer.Ordinal);
            SubscribeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            Adapter = Substitute.For<IMarketDataAdapter>();
            Adapter
                .SubscribeTicker(Arg.Any<Symbol>())
                .Returns(callInfo =>
                {
                    var normalized = callInfo.Arg<Symbol>().Normalized;
                    SubscribeCounts.AddOrUpdate(normalized, 1, (_, count) => count + 1);
                    if (FreshLanePerSubscribe)
                    {
                        // A revived stream re-runs the adapter's stream
                        // start: the old subject dies with its faulting
                        // start, the fresh lane is the only live one.
                        Feeds[normalized] = new Subject<Ticker>();
                    }

                    return LaneFor(Feeds, normalized);
                });
            Selections = new Subject<Symbol>();
            Pulse = new Subject<long>();
            Logger = new RecordingLogger();
            Feed = new WatchlistFeedService(
                Adapter,
                Selections,
                Catalog,
                Logger,
                Pulse,
                retryPolicy);
        }

        public string Directory { get; }

        public SettingsService Settings { get; }

        public WatchlistCatalogService Catalog { get; }

        public IMarketDataAdapter Adapter { get; }

        public ConcurrentDictionary<string, Subject<Ticker>> Feeds { get; }

        public ConcurrentDictionary<string, int> SubscribeCounts { get; }

        public Subject<Symbol> Selections { get; }

        public Subject<long> Pulse { get; }

        public RecordingLogger Logger { get; }

        public WatchlistFeedService Feed { get; }

        private bool FreshLanePerSubscribe { get; }

        /// <summary>How many times the adapter was asked to price the symbol.</summary>
        public int SubscribeCount(string symbolText) =>
            SubscribeCounts.TryGetValue(Symbol.Parse(symbolText).Normalized, out var count) ? count : 0;

        /// <summary>Push one ticker into the row's lane.</summary>
        public void Tick(string symbolText, decimal price, decimal changePercent, decimal volume24h, decimal bid, decimal ask)
        {
            LaneFor(Feeds, symbolText).OnNext(new Ticker(
                Symbol.Parse(symbolText),
                price,
                bid,
                ask,
                volume24h,
                changePercent,
                DateTimeOffset.UtcNow));
        }

        /// <summary>Fail the row's live lane exactly as a faulting stream start surfaces.</summary>
        public void Fail(string symbolText, Exception error) => LaneFor(Feeds, symbolText).OnError(error);

        /// <summary>Take one pulse-coalesced snapshot (subscriptions are synchronous).</summary>
        public IReadOnlyList<WatchlistQuote> SnapshotAfterPulse()
        {
            IReadOnlyList<WatchlistQuote> latest = Array.Empty<WatchlistQuote>();
            using (Feed.Quotes.Subscribe(snapshot => latest = snapshot))
            {
                Pulse.OnNext(0L);
            }

            return latest;
        }

        /// <summary>Wait until the predicate holds or the budget runs out (the ladder is async).</summary>
        public async Task WaitForAsync(Func<bool> condition, TimeSpan budget)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < budget)
            {
                await Task.Delay(10);
            }
        }

        public void Dispose()
        {
            Feed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Catalog.Dispose();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // The store's file watcher can hold the directory a beat
                // longer on some platforms — the temp directory dies with
                // the OS cleanup either way.
            }
        }

        /// <summary>The lane cache behind the substituted SubscribeTicker.</summary>
        private static Subject<Ticker> LaneFor(ConcurrentDictionary<string, Subject<Ticker>> feeds, string normalized) =>
            feeds.GetOrAdd(normalized, _ => new Subject<Ticker>());
    }

    /// <summary>A logger recording (level, text) pairs for the failure-family pins.</summary>
    private sealed class RecordingLogger : ILogger<WatchlistFeedService>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public string AllText => string.Join(Environment.NewLine, Entries.Select(entry => entry.Text));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    #endregion

    #region Feed Facts

    [Fact]
    public void TheFeed_HoldsUntilTheFirstPulse_ThenShipsTheStarterRowSetUnpriced()
    {
        using var fixture = new Fixture();
        var seen = new List<IReadOnlyList<WatchlistQuote>>();

        using (fixture.Feed.Quotes.Subscribe(seen.Add))
        {
            seen.Should().BeEmpty("nothing ships before the first pulse beat");
            fixture.Pulse.OnNext(0L);
            // A pulse without fresh data tick re-ships nothing (Sample clears
            // its buffer too): one snapshot per set of actually-new rows.
            fixture.Pulse.OnNext(1L);
        }

        seen.Should().ContainSingle();
        var snapshot = seen[0];
        snapshot.Should().HaveCount(StarterTexts.Length);
        snapshot.Select(row => row.SymbolText).Should().Equal(StarterTexts);
        foreach (var row in snapshot)
        {
            row.LastPrice.Should().BeNull("a row that has not ticked yet carries null fields");
            row.Change24hPercent.Should().BeNull();
            row.Volume24h.Should().BeNull();
            row.Sparkline.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task TheDemoCatalog_CoversEveryStarterRow()
    {
        // The default first-run exchange is the demo adapter (T-166): every
        // starter row must price there, or the row throws inside the ticker
        // projection at every startup and stays unpriced forever.
        var adapter = new DemoMarketDataAdapter(NullLogger<DemoMarketDataAdapter>.Instance);
        var symbols = await adapter.GetSymbolsAsync();

        StarterTexts.Should().OnlyContain(
            starter => symbols.Any(symbol => symbol.Normalized == starter),
            "the demo catalog is the default first-run exchange's tradable universe");
    }

    [Fact]
    public void ATickFollowedByAPulse_LandsTheRowsNewValues()
    {
        using var fixture = new Fixture();

        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);

        var snapshot = fixture.SnapshotAfterPulse();
        var btc = snapshot.Should().Contain(row => row.SymbolText == "BTC/USDT").Which;
        btc.LastPrice.Should().Be(65432.10m);
        btc.Change24hPercent.Should().Be(1.25m);
        btc.Volume24h.Should().Be(1_520_000m);
        btc.Sparkline.Should().Equal(new decimal[] { 65432.10m });
    }

    [Fact]
    public void ThePriceRing_CapsAtItsCapacity_KeepingTheNewestPrices()
    {
        using var fixture = new Fixture();

        for (var index = 1; index <= 34; index++)
        {
            fixture.Tick("ETH/USDT", index, 0m, 1m, 1m, 1m);
        }

        var eth = fixture.SnapshotAfterPulse()
            .Should().Contain(row => row.SymbolText == "ETH/USDT").Which;

        eth.Sparkline.Should().HaveCount((int)RingCapacity);
        eth.Sparkline[0].Should().Be(3m, "34 ticks over a 32-slot ring evict the two oldest prices");
        eth.Sparkline[^1].Should().Be(34m);
    }

    [Fact]
    public void ASelectionJoinsItsRow_AndTheNextSelectionRetiresIt()
    {
        using var fixture = new Fixture();

        fixture.Selections.OnNext(Symbol.Parse("TRX/USDT"));

        var joined = fixture.SnapshotAfterPulse();
        joined.Should().HaveCount(StarterTexts.Length + 1);
        joined.Select(row => row.SymbolText).Should().Contain("TRX/USDT").And.HaveCount(StarterTexts.Length + 1);

        fixture.Selections.OnNext(Symbol.Parse("ETH/USDT"));

        var after = fixture.SnapshotAfterPulse();
        after.Select(row => row.SymbolText).Should().Equal(StarterTexts);
    }

    [Fact]
    public async Task AnAddedSymbol_AppendsToTheRowSet()
    {
        using var fixture = new Fixture();

        await fixture.Catalog.AddSymbolAsync("Majors", "TRX/USDT");

        var snapshot = fixture.SnapshotAfterPulse();
        snapshot.Select(row => row.SymbolText)
            .Should().Equal([.. StarterTexts, "TRX/USDT"], "the append lands at the list's end");
    }

    [Fact]
    public async Task AMove_ReordersTheRows_WithTheTickStateSurviving()
    {
        using var fixture = new Fixture();
        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);

        await fixture.Catalog.MoveSymbolAsync("Majors", "BTC/USDT", 1);

        var snapshot = fixture.SnapshotAfterPulse();
        snapshot.Select(row => row.SymbolText).Should().StartWith("ETH/USDT").And.Contain("BTC/USDT");
        var btc = snapshot.Should().Contain(row => row.SymbolText == "BTC/USDT").Which;
        btc.LastPrice.Should().Be(65432.10m, "the moved row keeps its own pricing state");
        btc.Sparkline.Should().Equal(new decimal[] { 65432.10m });
    }

    [Fact]
    public async Task AListSwitch_SwapsTheRowSet_InFull()
    {
        using var fixture = new Fixture();

        await fixture.Catalog.CreateListAsync("Alts");
        await fixture.Catalog.AddSymbolAsync("Alts", "TRX/USDT");

        var alts = fixture.SnapshotAfterPulse();
        alts.Select(row => row.SymbolText).Should().Equal(["TRX/USDT"], "the active list IS the row set");

        await fixture.Catalog.SetActiveAsync("Majors");

        var majors = fixture.SnapshotAfterPulse();
        majors.Select(row => row.SymbolText).Should().Equal(StarterTexts);
    }

    [Fact]
    public async Task TheOverlay_RePinsAcrossAListSwitch_AsTheTrailingRow()
    {
        using var fixture = new Fixture();
        fixture.Selections.OnNext(Symbol.Parse("TRX/USDT"));

        await fixture.Catalog.CreateListAsync("Alts");

        var alts = fixture.SnapshotAfterPulse();
        alts.Select(row => row.SymbolText)
            .Should().Equal(["TRX/USDT"], "the empty list's set is the selection's overlay alone");

        await fixture.Catalog.SetActiveAsync("Majors");

        var majors = fixture.SnapshotAfterPulse();
        majors.Select(row => row.SymbolText)
            .Should().Equal([.. StarterTexts, "TRX/USDT"], "the overlay trails the list rows again");
    }

    [Fact]
    public void ASelectionOntoListRows_RetiresNothing()
    {
        using var fixture = new Fixture();

        fixture.Selections.OnNext(Symbol.Parse("BTC/USDT"));
        fixture.Selections.OnNext(Symbol.Parse("ETH/USDT"));

        var snapshot = fixture.SnapshotAfterPulse();
        snapshot.Should().HaveCount(StarterTexts.Length, "list rows never retire from selection moves");
        snapshot.Select(row => row.SymbolText).Should().Equal(StarterTexts);
    }

    #endregion

    #region Ticker Retry Facts

    [Fact]
    public async Task ATickerStreamFailure_LogsOnceAndTheRetryRevivesTheRow()
    {
        using var fixture = new Fixture(freshLanePerSubscribe: true, retryPolicy: TinyRetryPolicy);
        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);

        fixture.Fail("BTC/USDT", new IOException("the venue socket force-cycled"));

        await fixture.WaitForAsync(
            () => fixture.SubscribeCount("BTC/USDT") >= 2,
            TimeSpan.FromSeconds(2));
        fixture.Tick("BTC/USDT", 65500m, 1.30m, 1_521_000m, 65470m, 65520m);

        var snapshot = fixture.SnapshotAfterPulse();
        var btc = snapshot.Should().Contain(row => row.SymbolText == "BTC/USDT").Which;
        btc.LastPrice.Should().Be(65500m, "the revived stream re-prices the row without a UI nudge");
        btc.Sparkline.Should().Equal(
            new decimal[] { 65432.10m, 65500m },
            "the sparkline history survives the stream death");
        fixture.Logger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Text.Contains("ticker stream for BTC/USDT failed"))
            .Should().Be(1, "the death logs ONCE — the retry never repeats it");
        fixture.Logger.Entries.Should().NotContain(
            entry => entry.Text.Contains("failed again"),
            "a single failure never enters the ladder's debug churn");
    }

    [Fact]
    public async Task ARevivedStream_ResetsTheLadder_TheNextDeathLogsItsOwnWarning()
    {
        using var fixture = new Fixture(freshLanePerSubscribe: true, retryPolicy: TinyRetryPolicy);
        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);

        fixture.Fail("BTC/USDT", new IOException("first death"));
        await fixture.WaitForAsync(
            () => fixture.SubscribeCount("BTC/USDT") >= 2,
            TimeSpan.FromSeconds(2));
        fixture.Tick("BTC/USDT", 65500m, 1.30m, 1_521_000m, 65470m, 65520m);

        fixture.Fail("BTC/USDT", new IOException("second death"));
        await fixture.WaitForAsync(
            () => fixture.SubscribeCount("BTC/USDT") >= 3,
            TimeSpan.FromSeconds(2));

        fixture.Logger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Text.Contains("ticker stream for BTC/USDT failed"))
            .Should().Be(2, "each death earns its own warning — the revival reset the ladder");
        fixture.Logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Debug && entry.Text.Contains("failed again"),
            "a revived stream restarts the attempts from one");
    }

    [Fact]
    public async Task APersistentTickerFailure_LogsTheDeathOnceAndStaysOnTheDebugLadder()
    {
        using var fixture = new Fixture(freshLanePerSubscribe: true, retryPolicy: TinyRetryPolicy);
        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);
        var death = new IOException("the venue keeps refusing the stream");

        // Every future start fails immediately: the persistent-refusal
        // shape the ladder must survive without ever repeating the warning.
        fixture.Adapter
            .SubscribeTicker(Arg.Any<Symbol>())
            .Returns(Observable.Throw<Ticker>(death));

        fixture.Fail("BTC/USDT", death);

        await fixture.WaitForAsync(
            () => fixture.Logger.Entries.Count(entry =>
                entry.Level == LogLevel.Debug &&
                entry.Text.Contains("failed again")) >= 2,
            TimeSpan.FromSeconds(2));

        var snapshot = fixture.SnapshotAfterPulse();
        var btc = snapshot.Should().Contain(row => row.SymbolText == "BTC/USDT").Which;
        btc.LastPrice.Should().Be(65432.10m, "the row keeps its last served data across the whole ladder");
        btc.Sparkline.Should().Equal(new decimal[] { 65432.10m });
        fixture.Logger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Text.Contains("ticker stream for BTC/USDT failed"))
            .Should().Be(1, "the death logs once; every later churn rides the debug level");
        fixture.Logger.Entries.Count(entry =>
                entry.Level == LogLevel.Debug &&
                entry.Text.Contains("failed again (attempt"))
            .Should().BeGreaterThanOrEqualTo(2, "the ladder keeps backing off on the debug level");
    }

    [Fact]
    public async Task TheLadderNeverResurrectsARetiredPump()
    {
        using var fixture = new Fixture(freshLanePerSubscribe: true, retryPolicy: TinyRetryPolicy);
        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);
        fixture.Fail("BTC/USDT", new IOException("first death"));
        await fixture.WaitForAsync(
            () => fixture.SubscribeCount("BTC/USDT") >= 2,
            TimeSpan.FromSeconds(2));
        fixture.Fail("BTC/USDT", new IOException("second death"));
        await fixture.WaitForAsync(
            () => fixture.SubscribeCount("BTC/USDT") >= 3,
            TimeSpan.FromSeconds(2));

        // The third death parks a retry inside the ladder's delay; the
        // retirement lands while it waits and must kill it on wake.
        fixture.Fail("BTC/USDT", new IOException("third death"));
        await fixture.Catalog.CreateListAsync("Alts");
        await fixture.Catalog.AddSymbolAsync("Alts", "TRX/USDT");
        await fixture.Catalog.SetActiveAsync("Alts");
        await fixture.WaitForAsync(
            () => !fixture.SnapshotAfterPulse().Any(row => row.SymbolText == "BTC/USDT"),
            TimeSpan.FromSeconds(2));
        var countAtRetirement = fixture.SubscribeCount("BTC/USDT");

        await Task.Delay(150);

        fixture.SubscribeCount("BTC/USDT").Should().Be(
            countAtRetirement,
            "the retired pump's parked retry died with the row — no ladder cycle ever re-subscribes a retired row");
    }

    #endregion
}
