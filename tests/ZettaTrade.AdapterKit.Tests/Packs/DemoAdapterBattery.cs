namespace ZettaTrade.AdapterKit.Tests.Packs;

using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.AdapterKit.Tests.Battery;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Infrastructure.Demo;

/// <summary>
/// The demo adapter's conformance battery run: the pack is the inline canned
/// surface itself — no transport rigs, no REST routes — so every leg rides
/// the deterministic inline captures and the always-on restore semantics.
/// </summary>
public sealed class DemoAdapterBattery : AdapterConformanceBattery
{
    /// <summary>Create the demo pack's battery.</summary>
    public DemoAdapterBattery()
        : base(AdapterCapturePack.FromDirectory("Demo"))
    {
    }

    /// <inheritdoc/>
    protected override ValueTask<AdapterSession> OpenSessionAsync(CancellationToken ct)
    {
        // The canned surface needs no rigs at all: one inline adapter instance.
        var adapter = new DemoMarketDataAdapter(NullLogger<DemoMarketDataAdapter>.Instance);
        return ValueTask.FromResult(AdapterSession.Open(adapter));
    }
}
