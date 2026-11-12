namespace ZettaTrade.App.Tests;

using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Xunit;

/// <summary>
/// The pump world the marshal-invariant and drain-driving facts rely on: a
/// posted callback never executes before its drain, and it lands exactly
/// once at the first drain.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class AvaloniaDispatcherPumpTests
{
    [Fact]
    public async Task DrainAsync_ExecutesPostedJobsExhaustively()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
        var seen = new List<string>();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => seen.Add("drained"));
        seen.Should().BeEmpty("posted work waits for the pump — it never executes spontaneously");

        await AvaloniaDispatcherPump.DrainAsync();

        seen.Should().ContainSingle("one drain executed one posted job");
    }

    /// <summary>
    /// The T-216 family pin: a job that parks the pump thread beyond the
    /// legacy 5 s drain bound (the Defender first-load-scan shape) must
    /// complete the drain — under the old bound this exact shape false-failed
    /// the theme-pin facts with a TimeoutException while the pump was healthy.
    /// The follow-up drain proves the pump kept serving after the slow job.
    /// </summary>
    [Fact]
    public async Task DrainAsync_CompletesThroughASlowJobAndThePumpKeepsServing()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
        var slowRan = false;

        // 5.5 s: above the legacy 5 s bound (the shape that false-failed the
        // gate), below the shipped 20 s bound. The sleep parks the pump thread
        // INSIDE the job — the drain's completion cannot be served before it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(5_500));
            slowRan = true;
        });

        var drain = () => AvaloniaDispatcherPump.DrainAsync();
        await drain.Should().NotThrowAsync("a slow-but-healthy drain is served, never a false hang");

        slowRan.Should().BeTrue("the drain's completion records AFTER the slow job ran");

        var followUp = new List<string>();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => followUp.Add("served"));
        await AvaloniaDispatcherPump.DrainAsync();
        followUp.Should().ContainSingle("the pump serves the next drain after a slow one");
    }
}
