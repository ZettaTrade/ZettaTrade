namespace ZettaTrade.App.Tests.ViewModels;

using Xunit;

/// <summary>
/// The Avalonia-dispatcher classes of App.Tests run strictly sequentially:
/// the process shares ONE Avalonia dispatcher, and its first-touch owner
/// thread decides how every <c>ObserveOnUiThread</c> notification executes.
/// ChartViewModelTests pins that owner to a foreign (exited) thread once, so
/// each of these classes' facts see a deterministic posting world — one
/// class's fact must never drain or pin the dispatcher behind another
/// class's fact's back.
/// </summary>
/// <remarks>
/// T-138 widened the contract to the whole assembly: the pump installs at
/// the xUnit TEST-FRAMEWORK hook (<see cref="DispatcherFirstTestFramework"/>,
/// instantiated before any discovery reflection) so its thread always wins
/// the dispatcher's first-touch binding, and every
/// dispatcher-contact class (the pump facts, the drain-driving VM classes,
/// and the VM classes whose services may post internally) carries the
/// explicit <see cref="AvaloniaDispatcherSerial"/> membership. The
/// recorded poison shape — a dispatcher whose owner is a foreign,
/// long-exited thread: EVERY pump drain fails with "The calling thread
/// cannot access this object" and the serial block cascades — died with
/// the deterministic install order.
/// </remarks>
[CollectionDefinition("AvaloniaDispatcherSerial")]
public sealed class AvaloniaDispatcherSerialCollection;
