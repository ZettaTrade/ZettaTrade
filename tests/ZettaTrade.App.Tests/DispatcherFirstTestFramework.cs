using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using ZettaTrade.App.Tests.Views.Headless;

// The assembly's FIRST executable hook (T-138): xUnit instantiates the
// custom test framework before ANY discovery reflection runs, so the pump
// claims the dispatcher ownership before even MemberData property getters
// can touch Dispatcher.UIThread. A [ModuleInitializer] was tried first and
// REFUTED by the hang dump: the module initializer blocked on the pump
// thread's ready signal while the runtime refuses to run same-assembly
// code (the pump's RunPump) until the module initializer completes - a
// guaranteed self-deadlock (2/2 certification runs hung at discovery with
// zero tests started). The framework hook has no such gate: by the time it
// executes, the assembly is fully initialized, and the install's blocking
// wait is served by a dedicated background thread from ANOTHER call site.
[assembly: TestFramework("ZettaTrade.App.Tests.DispatcherFirstTestFramework", "ZettaTrade.App.Tests")]

namespace ZettaTrade.App.Tests;

/// <summary>
/// The xUnit test framework that installs the Avalonia dispatcher pump
/// before anything else of this assembly runs: discovery reflection,
/// MemberData getters, test-class static constructors — all of them come
/// later, so the pump thread always wins the dispatcher's first-touch
/// binding (the T-101 / T-127 / T-104 tally-flake family died with this
/// ordering). The headless world stands up HERE too: no test class can
/// ever win the Application.Current race with a bare, non-headless
/// setup — the shape that synchronized the window-materializing facts
/// into a mass collapse when a probe class ran first.
/// </summary>
public sealed class DispatcherFirstTestFramework : XunitTestFramework
{
    /// <summary>Install the pump world (idempotent) before any discovery.</summary>
    /// <param name="messageSink">The runner's message sink, per the xUnit contract.</param>
    public DispatcherFirstTestFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        AvaloniaDispatcherPump.EnsureInstalled();

        // The app materializes before any class runs: Application.Current
        // is always the headless world (Fluent, the Livery tokens, the
        // ReactiveUI fetcher, the real localization manager), so the
        // probe classes' bare SetupWithoutStarting never fires and the
        // window-materializing facts never meet a world without the
        // headless platform. Idempotent (the cached task) and its work
        // runs on the pump's owner thread.
        HeadlessWorld.EnsureAppAsync().GetAwaiter().GetResult();
    }
}
