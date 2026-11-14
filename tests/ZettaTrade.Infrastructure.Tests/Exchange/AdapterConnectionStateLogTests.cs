namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The connection-state log discipline (T-233): a connection-state line is a
/// REAL lifecycle transition, never the WS client's seeded pre-dial false.
/// The T-232 beta-prep finale's soak run failed at the T-141 isolation census
/// because the market adapters logged the seed at Information — a constructed
/// but never-dialed adapter printed its real endpoint
/// (<c>wss://stream.bybit.com/v5/public/spot</c>) into a demo-only run's log.
/// These facts pin the fix over the default (real-venue) endpoints, which is
/// exactly the soak's composition shape: construction without a dial produces
/// zero connection-state lines. The real-dial lifecycle lines (Connected on
/// the open, Disconnected on a drop) stay the WS E2E family's territory.
/// </summary>
public sealed class AdapterConnectionStateLogTests
{
    [Fact]
    public async Task Binance_construction_without_a_dial_logs_no_connection_state_line()
    {
        var logger = new RecordingLogger<BinanceAdapter>();

        await using var adapter = new BinanceAdapter(logger, Substitute.For<IHttpClientFactory>());

        logger.Lines.Should().NotContain(line => line.Contains("connection state"));
    }

    [Fact]
    public async Task Bybit_construction_without_a_dial_logs_no_connection_state_line()
    {
        var logger = new RecordingLogger<BybitAdapter>();

        await using var adapter = new BybitAdapter(logger, Substitute.For<IHttpClientFactory>());

        logger.Lines.Should().NotContain(line => line.Contains("connection state"));
    }

    [Fact]
    public async Task Okx_construction_without_a_dial_logs_no_connection_state_line()
    {
        var logger = new RecordingLogger<OkxAdapter>();

        await using var adapter = new OkxAdapter(logger, Substitute.For<IHttpClientFactory>());

        logger.Lines.Should().NotContain(line => line.Contains("connection state"));
    }

    [Fact]
    public async Task Alpaca_construction_without_a_dial_logs_no_connection_state_line()
    {
        var logger = new RecordingLogger<AlpacaAdapter>();

        await using var adapter = new AlpacaAdapter(
            logger,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ISecretStore>());

        logger.Lines.Should().NotContain(line => line.Contains("connection state"));
    }

    /// <summary>A logger recording the rendered lines, so the ctor's log lane stays assertable.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
