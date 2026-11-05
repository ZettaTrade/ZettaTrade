namespace ZettaTrade.App.Tests;

using System.Collections.Generic;
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
}
