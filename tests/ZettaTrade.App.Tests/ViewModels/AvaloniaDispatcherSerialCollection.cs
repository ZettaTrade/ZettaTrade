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
[CollectionDefinition("AvaloniaDispatcherSerial")]
public sealed class AvaloniaDispatcherSerialCollection;
