namespace ZettaTrade.Strategies.Tests.Host;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The host facts (T-111): one evaluation loop, one stamped signal per
/// (strategy, series) per ACCEPTED closed candle - the demo golden rides
/// the channel end to end on the same seeded candles the pure walk pins;
/// a throwing strategy is quarantined for the session while the healthy
/// one keeps evaluating; stale or duplicate candles are ignored, never
/// re-evaluated; the window is capped; two series never share a window;
/// multiple strategies emit in catalog order; a cancelled pump still
/// completes the output. The channel is the whole story: inert output,
/// read by the consumer, never wired into orders.
/// </summary>
public sealed class StrategyHostTests
{
    #region Constants

    /// <summary>Bounded budget the pump must finish within (the loop is local work; timing feeds nothing).</summary>
    private static readonly TimeSpan PumpBudget = TimeSpan.FromSeconds(10);

    #endregion

    [Fact]
    public async Task TheHost_Golden_EmitOneStampedSignalPerDemoStancePerClosedCandle()
    {
        var host = new StrategyHost(
            [new DemoGoldenCrossStrategy()],
            NullLogger<StrategyHost>.Instance);
        var candles = ClosedCandles(TestBars.GoldenCandles());

        var signals = await RunAndDrainAsync(host, candles);

        signals.Should().HaveCount(candles.Length, "one accepted candle per strategy, the demo is the only strategy");

        var expected = TestBars.ExpectedGoldenStances;
        for (var k = 0; k < expected.Length; k++)
        {
            var signal = signals[k];
            signal.StrategyId.Should().Be(DemoGoldenCrossStrategy.StrategyId);
            signal.Symbol.Should().Be(TestBars.Symbol);
            signal.Interval.Should().Be(TimeFrame.M1);
            signal.Candle.Should().Be(candles[k].Bar, "the stamp names the candle the evaluation ran on");
            signal.Stance.Should().Be(expected[k], "the channel golden carries the walk's stance at candle {0}", k);
        }
    }

    [Fact]
    public async Task TheHost_MultipleStrategies_EmitInCatalogOrderPerCandle()
    {
        var host = new StrategyHost(
            [new FixedStanceStrategy("first", StrategyStance.Long), new FixedStanceStrategy("second", StrategyStance.Flat)],
            NullLogger<StrategyHost>.Instance);
        var candles = ClosedCandles(TestBars.Candles(1, 2, 3, 4, 5, 6, 7, 8, 9));

        var signals = await RunAndDrainAsync(host, candles);

        signals
            .Select(signal => signal.StrategyId)
            .Should().Equal(
                candles.SelectMany(_ => new[] { "first", "second" }),
                "every closed candle evaluates the strategies in the order the catalog hands them");
        signals[0].Stance.Should().Be(StrategyStance.Long);
        signals[1].Stance.Should().Be(StrategyStance.Flat);
    }

    /// <summary>
    /// The T-054 tolerance canon at runtime: a strategy that throws is
    /// logged ONCE and quarantined for the session - the healthy strategy
    /// keeps evaluating on every candle, the channel never dies.
    /// </summary>
    [Fact]
    public async Task TheHost_AThrowingStrategy_IsQuarantinedAndTheHealthyOneKeepsEvaluating()
    {
        var logger = new SpyLogger();
        var host = new StrategyHost(
            [new ThrowingStrategy("boom"), new FixedStanceStrategy("steady", StrategyStance.Long)],
            logger);
        var candles = ClosedCandles(TestBars.Candles(1, 2, 3, 4, 5, 6));

        var signals = await RunAndDrainAsync(host, candles);

        signals
            .Select(signal => signal.StrategyId)
            .Should().Equal(
                candles.SelectMany(_ => new[] { "steady" }),
                "only the healthy strategy reaches the channel after the first throw");
        signals.Should().AllSatisfy(signal => signal.Stance.Should().Be(StrategyStance.Long));

        logger.Entries.Where(entry => entry.Level == LogLevel.Error && entry.Message.Contains("boom"))
            .Should().ContainSingle("the first throw is loud; the quarantine keeps the session from re-echoing it");
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("steady"));
    }

    /// <summary>A late or duplicate closed candle does not advance a series - no re-evaluation, no extra signal.</summary>
    [Fact]
    public async Task TheHost_AStaleOrDuplicateCandle_IsIgnoredNotReevaluated()
    {
        var host = new StrategyHost(
            [new FixedStanceStrategy("watch", StrategyStance.Neutral)],
            NullLogger<StrategyHost>.Instance);
        var candles = ClosedCandles(TestBars.Candles(1, 2, 3, 4, 5, 6));

        var feed = candles.Select(candle => candle).ToList();
        feed.Add(candles[^1]);
        feed.Add(new ClosedCandle(TestBars.Symbol, TimeFrame.M1, candles[2].Bar));

        var signals = await RunAndDrainAsync(host, feed);

        signals.Should().HaveCount(
            candles.Length,
            "the stale tail and the duplicate advance nothing: accepted candles only, never re-evaluated");
        signals
            .Select(signal => signal.Candle.Time)
            .Should().HaveCount(candles.Length).And.OnlyHaveUniqueItems();
    }

    /// <summary>The window is capped: the evaluation sees at most the retained bars, oldest first.</summary>
    [Fact]
    public async Task TheHost_TheRetainedWindow_IsCappedAndOldestFirst()
    {
        const int windowBars = 4;
        var capturing = new CapturingStrategy("lens");
        var host = new StrategyHost([capturing], NullLogger<StrategyHost>.Instance, windowBars);
        var candles = ClosedCandles(TestBars.Candles(1, 2, 3, 4, 5, 6, 7));

        await RunAndDrainAsync(host, candles);

        var windows = capturing.Contexts.Select(context => context.ClosedBars.Select(bar => bar.Close).ToArray()).ToList();
        windows.Should().HaveCount(candles.Length, "every accepted candle evaluates once");
        windows.Select(window => window.Length)
            .Should().ContainInOrder(1, 2, 3, 4)
            .And.HaveCount(candles.Length)
            .And.OnlyContain(length => length <= windowBars, "the window never overflows its cap");
        windows[4].Should().Equal(new[] { 2m, 3m, 4m, 5m }, "the truncation keeps the NEWEST bars, oldest first");
        windows[^1].Should().Equal(new[] { 4m, 5m, 6m, 7m }, "the last evaluation sees exactly the retained window, oldest first");
    }

    /// <summary>Two series never share a window: each context carries its own series and bars.</summary>
    [Fact]
    public async Task TheHost_DifferentSeries_EvaluateTheirOwnWindows()
    {
        var capturing = new CapturingStrategy("lens");
        var host = new StrategyHost([capturing], NullLogger<StrategyHost>.Instance);

        await RunAndDrainAsync(host, CrossSeriesCandles(
            ("ETH/USDT", TestBars.Candles(2, 4, 6)),
            ("BTC/USDT", TestBars.Candles(1, 3, 5))).ToList());

        var eth = capturing.Contexts
            .Where(context => context.Symbol.Normalized == "ETH/USDT")
            .Select(context => context.ClosedBars.Select(bar => bar.Close).ToArray())
            .ToList();
        var btc = capturing.Contexts
            .Where(context => context.Symbol.Normalized == "BTC/USDT")
            .Select(context => context.ClosedBars.Select(bar => bar.Close).ToArray())
            .ToList();

        eth.Should().Equal(
            new[] { new[] { 2m }, new[] { 2m, 4m }, new[] { 2m, 4m, 6m } },
            (window, expected) => window.SequenceEqual(expected),
            "the ETH window holds ETH bars only");
        btc.Should().Equal(
            new[] { new[] { 1m }, new[] { 1m, 3m }, new[] { 1m, 3m, 5m } },
            (window, expected) => window.SequenceEqual(expected),
            "the BTC window holds BTC bars only");
    }

    /// <summary>A cancelled pump is the stop: the output completes and the drain terminates, no exception surfaces.</summary>
    [Fact]
    public async Task TheHost_ACancelledPump_CompletesTheOutputWithoutSurfacingTheStop()
    {
        var host = new StrategyHost(
            [new FixedStanceStrategy("steady", StrategyStance.Neutral)],
            NullLogger<StrategyHost>.Instance);
        var candles = ClosedCandles(TestBars.Candles(1, 2, 3, 4, 5, 6));
        foreach (var candle in candles)
        {
            host.CandleInput.TryWrite(candle);
        }

        var source = new CancellationTokenSource();
        source.Cancel();
        var drain = new List<StrategySignal>();
        var pump = host.RunAsync(source.Token).WaitAsync(PumpBudget);

        await foreach (var signal in host.Signals.ReadAllAsync())
        {
            drain.Add(signal);
        }

        await pump;

        host.Signals.Completion.IsCompleted.Should().BeTrue("the stop completes the output for the reader");
        drain.Should().HaveCountLessThanOrEqualTo(candles.Length, "whatever the pump delivered before the stop, the channel closes cleanly");
        drain.Should().NotContain(
            signal => signal.StrategyId != "steady",
            "whatever the pump delivered before the stop, only the healthy strategy reached the channel");
    }

    #region Fixture

    /// <summary>
    /// Run the pump to completion: write the candles, end the input, run
    /// the loop and drain the output. The drain happens off the pump, so
    /// the completed channel is read exactly once.
    /// </summary>
    /// <param name="host">The host under judgment.</param>
    /// <param name="candles">The closed candles to feed (the stale ones among them are the leg's own content).</param>
    private static async Task<List<StrategySignal>> RunAndDrainAsync(
        StrategyHost host,
        IReadOnlyList<ClosedCandle> candles)
    {
        var signals = new List<StrategySignal>();
        var drain = Task.Run(async () =>
        {
            await foreach (var signal in host.Signals.ReadAllAsync())
            {
                signals.Add(signal);
            }
        });

        foreach (var candle in candles)
        {
            host.CandleInput.TryWrite(candle);
        }

        host.CandleInput.TryComplete();
        await host.RunAsync().WaitAsync(PumpBudget);
        await drain.WaitAsync(PumpBudget);

        return signals;
    }

    /// <summary>The M1 closed candles for one series (ascending by construction of the seeds).</summary>
    private static ClosedCandle[] ClosedCandles(IReadOnlyList<Bar> bars) =>
        bars.Select(bar => new ClosedCandle(TestBars.Symbol, TimeFrame.M1, bar)).ToArray();

    /// <summary>Interleave two series' candles (the feeds may arrive in any series order).</summary>
    private static IEnumerable<ClosedCandle> CrossSeriesCandles(
        (string SymbolText, Bar[] Bars) first,
        (string SymbolText, Bar[] Bars) second)
    {
        for (var i = 0; i < first.Bars.Length; i++)
        {
            yield return new ClosedCandle(Symbol.Parse(first.SymbolText), TimeFrame.M1, first.Bars[i]);
            yield return new ClosedCandle(Symbol.Parse(second.SymbolText), TimeFrame.M1, second.Bars[i]);
        }
    }

    /// <summary>The constant-stance implementation: the order/quarantine legs need only an id and a verdict.</summary>
    private sealed class FixedStanceStrategy(string id, StrategyStance stance) : IStrategy
    {
        public string Id => id;

        public StrategyStance Evaluate(StrategyContext context) => stance;
    }

    /// <summary>The throwing implementation: the quarantine leg's own failure under test.</summary>
    private sealed class ThrowingStrategy(string id) : IStrategy
    {
        public string Id => id;

        public StrategyStance Evaluate(StrategyContext context) =>
            throw new InvalidOperationException("the quarantine fixture never evaluates");
    }

    /// <summary>The capturing implementation: the window legs record what an evaluation saw.</summary>
    private sealed class CapturingStrategy(string id) : IStrategy
    {
        public List<StrategyContext> Contexts { get; } = [];

        public string Id => id;

        public StrategyStance Evaluate(StrategyContext context)
        {
            Contexts.Add(context);
            return StrategyStance.Neutral;
        }
    }

    /// <summary>The logger spy: records the level + message pairs.</summary>
    private sealed class SpyLogger : ILogger<StrategyHost>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    #endregion
}
