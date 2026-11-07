namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Pins the deep-history backfill fetcher's machinery (T-086): one pass
/// walks the coverage window backwards in bounded segments whose bands
/// neither overlap nor leak outside the window, the RAW adapter page
/// persists write-through BEFORE its batch is emitted, an adapter answer
/// that is empty where bars were expected ends the pass with an
/// <c>EndOfHistory</c> floor batch (never a fabricated bar), the segment
/// budget ends its pass quietly without a floor claim, and the bounded
/// channel leg stalls the producer while a consumer parks. Pure machinery
/// facts — no view state involved.
/// </summary>
public sealed class CandleBackfillServiceTests
{
    /// <summary>Thursday, 2026-01-01 10:00 UTC — every fact's window runs
    /// off aligned opens from here (dense, weekend-agnostic).</summary>
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    #region Recording Rigs

    /// <summary>
    /// Market-data fake that answers every bars call with the full page its
    /// request window claims INCLUDING the upper edge (the boundary quirk an
    /// inclusive adapter carries; the service's band filter must conflate
    /// it, its limit cap cuts the edge again). Call indices can answer EMPTY
    /// (the feed's floor) or PARK on a gate (the backpressure and the
    /// cancellation facts).
    /// </summary>
    private sealed class RecordingBarsAdapter : IMarketDataAdapter
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = new();
        private int _callIndex;

        /// <summary>The call indices (0-based) that answer with an empty page.</summary>
        public HashSet<int> EmptyCallIndices { get; } = [];

        /// <summary>The request windows in call order, plus each call's token.</summary>
        public ConcurrentQueue<(DateTimeOffset From, DateTimeOffset? To, int Limit)> Calls { get; } = new();

        public ConcurrentQueue<CancellationToken> CallTokens { get; } = new();

        public string Id => "fake";

        public string Name => "Fake";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            var callIndex = _callIndex++;
            Calls.Enqueue((from, to, limit));
            CallTokens.Enqueue(ct);

            if (_gates.TryGetValue(callIndex, out var gate))
            {
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }

            if (EmptyCallIndices.Contains(callIndex))
            {
                return [];
            }

            var step = TimeFrameCalendar.NominalBarLength(timeFrame);
            var upper = to;
            var bars = new List<Bar>();
            for (var open = from; upper is null || open <= upper.Value; open = open.Add(step))
            {
                var index = (open - Base).Ticks / step.Ticks;
                bars.Add(new Bar(open, index + 1m, index + 2m, index, index + 1m, 5));
                if (bars.Count >= limit)
                {
                    break;
                }
            }

            return bars;
        }

        /// <summary>Gate one future call by its index: a fact parks the
        /// producer leg exactly at that segment.</summary>
        public void GateCall(int index, out TaskCompletionSource gate) =>
            _gates[index] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Calls.Count;

        #region Unused Market Data Surface

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => throw new NotSupportedException();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => throw new NotSupportedException();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            throw new NotSupportedException();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        #endregion
    }

    /// <summary>The ICandleCache fake: write-throughs trace in call order,
    /// the write arm faults on demand, the reads answer empty (the backfill
    /// never reads — the repair chain does, and its facts live elsewhere).</summary>
    private sealed class RecordingCandleCache : ICandleCache
    {
        public Exception? WriteFault { get; set; }

        /// <summary>The "adapter|symbol|frame|count" trace in call order.</summary>
        public ConcurrentQueue<string> Writes { get; } = new();

        public async Task WriteThroughAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            IReadOnlyList<Bar> bars,
            CancellationToken ct = default)
        {
            Writes.Enqueue($"{adapterId}|{symbol.Normalized}|{timeFrame}|{bars.Count}");
            if (WriteFault is { } fault)
            {
                throw fault;
            }

            await Task.CompletedTask;
        }

        #region Unused Cache Surface

        public async Task<IReadOnlyList<Bar>> GetLatestAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            int limit,
            CancellationToken ct = default) =>
            [];

        public async Task<IReadOnlyList<Bar>> GetOlderThanAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset before,
            int limit,
            CancellationToken ct = default) =>
            [];

        #endregion
    }

    private static CandleBackfillRequest Request(
        RecordingBarsAdapter adapter,
        TimeFrame frame,
        DateTimeOffset from,
        DateTimeOffset to) => new(
        adapter,
        BtcUsdt,
        frame,
        from,
        to);

    /// <summary>Drain the fetch sequence, recording every batch, the write-
    /// through count AT each batch's receipt and the floor count: the
    /// write-before-emit order is pinned by construction instead of
    /// inspecting a post-hoc world.</summary>
    private static async Task<(
        IReadOnlyList<Bar> Bars,
        List<CandleBackfillBatch> Batches,
        int Floors,
        List<int> WritesAtReceipt)> DrainAsync(
        CandleBackfillService service,
        CandleBackfillRequest request,
        RecordingCandleCache cache,
        CancellationToken ct = default)
    {
        List<Bar> collected = [];
        List<CandleBackfillBatch> batches = [];
        var floors = 0;
        List<int> writesAtReceipt = [];

        await foreach (var batch in service.FetchAsync(request, ct))
        {
            batches.Add(batch);
            writesAtReceipt.Add(cache.Writes.Count);
            if (batch.EndOfHistory)
            {
                floors++;
            }

            collected.AddRange(batch.Bars);
        }

        return (collected, batches, floors, writesAtReceipt);
    }

    /// <summary>Poll until the predicate holds (bounded) — the parked
    /// producer world settles within a bounded grace window, and the
    /// assertions below prove the settled world is ALSO frozen (a
    /// blocked producer write can only ever move on a consumer read).</summary>
    private static async Task<bool> SettledAsync(TimeSpan budget, Func<bool> predicate)
    {
        var stop = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < stop)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return predicate();
    }

    #endregion

    #region Segment Walk

    [Fact]
    public async Task ThePass_StitchesTheBandsAgainstTheWindow()
    {
        var adapter = new RecordingBarsAdapter();
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(700));

        var (bars, batches, floors, _) = await DrainAsync(service, window, new RecordingCandleCache());

        // The producer walks BACKWARDS from the window's exclusive upper
        // edge, so the newest full band emits first, the remainder second —
        // each band stitched inside its own batch; the caller re-forms the
        // ascending series by its own open-time union.
        batches.Should().HaveCount(2, "700 hourly opens fit two 500-bar segments stitched without overlap");
        batches.Should().OnlyContain(
            batch => batch.EndOfHistory == false && batch.DepthFloor == null,
            "nothing in the stitched pass proved a floor");
        batches[0].Bars.Select(bar => bar.Time).Should().Equal(
            Enumerable.Range(200, 500).Select(hour => Base.AddHours(hour)),
            "the newest band covers [200h, 700h), the window's exclusive upper edge cut");
        batches[1].Bars.Select(bar => bar.Time).Should().Equal(
            Enumerable.Range(0, 200).Select(hour => Base.AddHours(hour)),
            "the remainder covers [0h, 200h), the adapter's trailing inclusive edge cut");

        // The union is the window's own opens exactly: every open once, no
        // duplicate, no wick outside the request window.
        bars.Should().OnlyHaveUniqueItems(bar => bar.Time);
        bars.Select(bar => bar.Time).Should().BeEquivalentTo(
            Enumerable.Range(0, 700).Select(hour => Base.AddHours(hour)));
        adapter.Calls.Select(call => (call.From, call.To, call.Limit)).Should().Equal(
            new[]
            {
                (Base.AddHours(200), (DateTimeOffset?)Base.AddHours(700), 500),
                (Base, (DateTimeOffset?)Base.AddHours(200), 500),
            });
    }

    [Fact]
    public async Task TheRawPageWritesThroughBeforeItsBatchEmits()
    {
        var adapter = new RecordingBarsAdapter();
        var cache = new RecordingCandleCache();
        var service = new CandleBackfillService(cache, NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(1200));

        var (_, _, _, writesAtReceipt) = await DrainAsync(service, window, cache);

        writesAtReceipt.Should().Equal(
            [3, 3, 3],
            "the channel's second capacity slot lets one segment write ahead of the consumer, and every receipt still found its own band's write-through already recorded");
        cache.Writes.Should().Equal(
            [
                "fake|BTC/USDT|H1|500",
                "fake|BTC/USDT|H1|500",
                "fake|BTC/USDT|H1|201",
            ],
            "the write-through persists the RAW adapter page (the last band's inclusive edge rides along; the band filter cuts it from the batch)");
    }

    [Fact]
    public async Task TheEmptyBandEndsThePassWithTheFloorBatch()
    {
        var adapter = new RecordingBarsAdapter();
        _ = adapter.EmptyCallIndices.Add(0);
        var cache = new RecordingCandleCache();
        var service = new CandleBackfillService(cache, NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(400));

        var (bars, _, floors, _) = await DrainAsync(service, window, cache);

        bars.Should().BeEmpty("an empty answer never fabricates a substitute");
        floors.Should().Be(1);
        adapter.CallCount.Should().Be(1, "the empty answer ends the walk");
        cache.Writes.Should().BeEmpty("nothing the feed refused gets persisted either");
    }
    [Fact]
    public async Task TheFloorInsideTheWindowEndsThePassAfterTheCoverage()
    {
        var adapter = new RecordingBarsAdapter();
        _ = adapter.EmptyCallIndices.Add(1);
        var cache = new RecordingCandleCache();
        var service = new CandleBackfillService(cache, NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(1000));

        var (bars, _, floors, writesAtReceipt) = await DrainAsync(service, window, cache);

        bars.Select(bar => bar.Time).Should().Equal(
            Enumerable.Range(500, 500).Select(hour => Base.AddHours(hour)),
            "only the band above the floor got fetched");
        floors.Should().Be(1);
        adapter.Calls.Select(call => (call.From, call.To)).Should().Equal(
            new[]
            {
                (Base.AddHours(500), (DateTimeOffset?)Base.AddHours(1000)),
                (Base, (DateTimeOffset?)Base.AddHours(500)),
            });
        writesAtReceipt.Should().BeEquivalentTo(
            new[] { 1, 1 },
            "the batch above the floor rode after its write; the floor batch follows the same one");
    }

    [Fact]
    public async Task TheUpperBoundaryBarOfAnInclusiveAdapterStaysOutside()
    {
        var adapter = new RecordingBarsAdapter();
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(400));

        var (bars, _, _, _) = await DrainAsync(service, window, new RecordingCandleCache());

        bars.Should().HaveCount(400);
        bars.Select(bar => bar.Time).Should().NotContain(
            Base.AddHours(400),
            "the exclusive upper edge names the bar ABOVE the window — no boundary bar, no duplicate");
        adapter.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task TheSegmentBudgetEndsQuietlyWithoutAFloorClaim()
    {
        var adapter = new RecordingBarsAdapter();
        var upper = Base.AddMinutes(8001);
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);

        var (bars, _, floors, _) = await DrainAsync(
            service,
            Request(adapter, TimeFrame.M1, Base, upper),
            new RecordingCandleCache());

        bars.Should().HaveCount(16 * 500, "16 full minute-scale segments are all one pass serves");
        floors.Should().Be(0, "the budget exhaustion must not claim a floor it never proved");
        adapter.Calls.Should().HaveCount(16);
        adapter.Calls.Select(call => call.Limit).Should().OnlyContain(limit => limit == 500);
    }

    [Fact]
    public async Task TheCacheFaultIsBookkeepingNotAnEnd()
    {
        var adapter = new RecordingBarsAdapter();
        var cache = new RecordingCandleCache { WriteFault = new InvalidOperationException("disk on fire") };
        var service = new CandleBackfillService(cache, NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(1200));

        var (bars, _, floors, _) = await DrainAsync(service, window, cache);

        bars.Should().HaveCount(1200, "the pass delivered its stitched segments despite the faults");
        floors.Should().Be(0);
        cache.Writes.Should().HaveCount(3, "every band recorded its faulting write-through attempt");
    }

    #endregion

    #region Bounded Leg

    [Fact]
    public async Task ASlowConsumerStallsTheProducerAtTheChannelCapacity()
    {
        var adapter = new RecordingBarsAdapter();
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(3600));
        var cancellation = new CancellationTokenSource();

        var enumerator = service.FetchAsync(window, cancellation.Token).GetAsyncEnumerator();
        try
        {
            // The first read frees a slot; without any further read the
            // producer keeps fetching EXACTLY one segment ahead: it froze
            // mid-write holding the bounded channel's full capacity.
            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
                .Should().BeTrue("the first stitched segment arrives while the producer runs ahead");

            (await SettledAsync(TimeSpan.FromSeconds(2), () => adapter.CallCount >= 4))
                .Should().BeTrue("the producer fetched into both capacity slots plus one blocked write");

            adapter.CallCount.Should().Be(
                4,
                "a blocked write pins the producer: the fifth window stays unreachable while the consumer parks");

            // One more read frees a slot, the blocked write lands and the
            // producer advances exactly ONE segment deeper before freezing
            // in the same blocked-write state.
            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
                .Should().BeTrue();

            (await SettledAsync(TimeSpan.FromSeconds(2), () => adapter.CallCount >= 5))
                .Should().BeTrue("one read moved the producer exactly one batch deeper");

            adapter.CallCount.Should().Be(5, "the producer is parked mid-write into the full channel again");
        }
        finally
        {
            await enumerator.DisposeAsync();
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task ACanceledConsumerStopsThePendingRead()
    {
        var adapter = new RecordingBarsAdapter();
        adapter.GateCall(1, out var secondCallGate);
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);
        var window = Request(adapter, TimeFrame.H1, Base, Base.AddHours(1200));
        var cancellation = new CancellationTokenSource();

        var enumerator = service.FetchAsync(window, cancellation.Token).GetAsyncEnumerator();
        var secondRead = async () => await enumerator
            .MoveNextAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
                .Should().BeTrue();

            cancellation.Cancel();

            await secondRead.Should().ThrowAsync<OperationCanceledException>(
                "the pending read surfaces the consumer's token, never a hung enumeration");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // The gated producer returns and completes by riding its fault on
        // the channel (never a live task fault), so the fact ends leak-free.
        secondCallGate.TrySetResult();
        adapter.CallTokens.Should().Contain(
            token => token.IsCancellationRequested,
            "the window the pass fetched behind the consumer carried the token");
    }

    #endregion

    #region Request Guard

    [Fact]
    public async Task TheDegenerateWindowIsRejectedOnTheFirstRead()
    {
        var adapter = new RecordingBarsAdapter();
        var service = new CandleBackfillService(new RecordingCandleCache(), NullLogger<CandleBackfillService>.Instance);

        var enumerate = async () => await service
            .FetchAsync(Request(adapter, TimeFrame.H1, Base.AddHours(10), Base))
            .GetAsyncEnumerator()
            .MoveNextAsync();

        await enumerate.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "an upper edge below the needed oldest open trips the caller's contract, never a silent no-op");
    }

    #endregion
}
