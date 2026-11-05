namespace ZettaTrade.App.Tests;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Serilog;
using Serilog.Events;
using Xunit;

/// <summary>
/// BUG-019: a startup failure may not vanish unlogged. The composition root
/// configures the Serilog static logger as its very first step, so every
/// failure from that point on is recordable:
/// <c>OnFrameworkInitializationCompleted</c> writes the entry through
/// <see cref="App.LogStartupFailure"/> before its fail-fast rethrow, and the
/// entry carries the exception instance the process dies with.
/// </summary>
public sealed class AppBootstrapTests
{
    [Fact]
    public void LogStartupFailure_WritesTheErrorEntryWithTheException()
    {
        // Arrange: an in-memory sink installed as the global logger — the
        // exact record of what a failed startup would reach the file log
        // with. Swapped per fact and restored in finally: the static logger
        // is global state other facts never consult.
        var originalLogger = Log.Logger;
        var events = new List<LogEvent>();
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Sink(new InMemorySink(events))
            .CreateLogger();
        try
        {
            var failure = new InvalidOperationException("the settings store is corrupt");

            // Act
            App.LogStartupFailure(failure);

            // Assert: exactly one ERROR entry whose raw exception is ours.
            events.Should().HaveCount(1);
            var entry = events.Single();
            entry.Level.Should().Be(LogEventLevel.Error, "a startup crash is an error-grade event");
            entry.Exception.Should().BeSameAs(failure);
            entry.RenderMessage().Should().Contain("startup");
        }
        finally
        {
            Log.Logger = originalLogger;
        }
    }

    /// <summary>The smallest listener: keep every emitted event.</summary>
    private sealed class InMemorySink : Serilog.Core.ILogEventSink
    {
        private readonly List<LogEvent> _events;

        public InMemorySink(List<LogEvent> events) => _events = events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
