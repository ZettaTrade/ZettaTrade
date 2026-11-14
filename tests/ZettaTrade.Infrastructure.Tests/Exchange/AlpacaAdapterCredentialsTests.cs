namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// The Alpaca credentials surface (T-227): a missing secret-store pair
/// refuses every authorized REST surface with the typed absence error (the
/// request never dials the wire), the standing absence answers ONE quiet
/// information line per adapter session (the repeated refusals ride
/// silently — the palette and the rest of the consumers keep the log free
/// of a per-call failure family), and a present pair serves the request
/// authenticated with no absence line anywhere.
/// </summary>
public sealed class AlpacaAdapterCredentialsTests
{
    #region Helpers

    /// <summary>Symbol used in every scenario; its exchange form is "AAPL".</summary>
    private static readonly Symbol Aapl = Symbol.Parse("AAPL");

    /// <summary>The local test hosts: the stub intercepts every dial, the URLs pin the record's shape.</summary>
    private static readonly AlpacaEndpoints TestEndpoints = new(
        RestBaseUrl: "http://alpaca-rest.test",
        CatalogRestBaseUrl: "http://alpaca-catalog.test",
        WsUrl: "ws://alpaca-stream.test/stream");

    /// <summary>An empty one-page bars document: the authenticated request's honest answer.</summary>
    private const string EmptyBarsJson = """{"bars":[]}""";

    /// <summary>
    /// A capturing handler: the wire-never-dialed pin and the authenticated
    /// headers' pin both read through it.
    /// </summary>
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastRequest = request;
            return respond(request);
        }
    }

    /// <summary>
    /// A capturing logger: the quiet-absence pin asserts the level family
    /// (one information line, zero warnings) rather than exact wording.
    /// </summary>
    private sealed class RecordingLogger : ILogger<AlpacaAdapter>
    {
        /// <summary>Every logged entry so far, in arrival order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>The stub secret store: an explicit pair set, empty when the absence scenarios ask.</summary>
    private sealed class StubSecretStore(IReadOnlyDictionary<string, string> pairs) : Core.Interfaces.ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(pairs.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static (AlpacaAdapter Adapter, CapturingHandler Handler, RecordingLogger Logger) BuildAdapter(
        IReadOnlyDictionary<string, string> secretPairs)
    {
        var logger = new RecordingLogger();
        var handler = new CapturingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(EmptyBarsJson),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var adapter = new AlpacaAdapter(logger, factory, new StubSecretStore(secretPairs), TestEndpoints);
        return (adapter, handler, logger);
    }

    #endregion Helpers

    #region The standing absence

    [Fact]
    public async Task TheMissingPair_RefusesTheWireWithTheTypedException()
    {
        var (adapter, handler, _) = BuildAdapter(new Dictionary<string, string>());

        Func<Task> first = () => adapter.GetBarsAsync(Aapl, TimeFrame.M1, DateTimeOffset.UtcNow);
        (await first.Should().ThrowAsync<AdapterCredentialsMissingException>())
            .Which.AdapterId.Should().Be("alpaca");
        Func<Task> second = () => adapter.GetBarsAsync(Aapl, TimeFrame.M1, DateTimeOffset.UtcNow);
        (await second.Should().ThrowAsync<AdapterCredentialsMissingException>())
            .Which.SecretKeys.Should().Contain(
                AlpacaAdapter.ApiKeySecretKey,
                AlpacaAdapter.ApiSecretSecretKey);

        handler.Requests.Should().Be(0, "the absence refuses before any dial");
    }

    [Fact]
    public async Task TheMissingPair_ReportsOneQuietLinePerSession()
    {
        var (adapter, _, logger) = BuildAdapter(new Dictionary<string, string>());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await FluentActions.Awaiting(
                () => adapter.GetBarsAsync(Aapl, TimeFrame.M1, DateTimeOffset.UtcNow))
                .Should().ThrowAsync<AdapterCredentialsMissingException>();
        }

        logger.Entries
            .Count(entry => entry.Level == LogLevel.Information &&
                entry.Message.Contains("runs without market-data credentials"))
            .Should().Be(1, "the standing absence is one honest line, never a per-call family");
        logger.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Should().BeEmpty("an absence is not a warning family");
        logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Information &&
                entry.Message.Contains("runs without market-data credentials"));
        logger.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Should().BeEmpty("an absence is not a warning family");
    }

    [Fact]
    public async Task TheSavedPair_ServesTheRequestAuthenticatedAndQuiet()
    {
        var (adapter, handler, logger) = BuildAdapter(new Dictionary<string, string>
        {
            [AlpacaAdapter.ApiKeySecretKey] = "cred-test-key",
            [AlpacaAdapter.ApiSecretSecretKey] = "cred-test-secret",
        });

        await adapter.GetBarsAsync(Aapl, TimeFrame.M1, DateTimeOffset.UtcNow, limit: 1);

        handler.Requests.Should().Be(1, "the whole pair authorizes the request");
        handler.LastRequest!.Headers.GetValues("APCA-API-KEY-ID").Should().Contain("cred-test-key");
        handler.LastRequest.Headers.GetValues("APCA-API-SECRET-KEY").Should().Contain("cred-test-secret");
        logger.Entries.Should().NotContain(
            entry => entry.Message.Contains("credentials"),
            "an authenticated session has no absence line");
    }

    #endregion The standing absence
}
