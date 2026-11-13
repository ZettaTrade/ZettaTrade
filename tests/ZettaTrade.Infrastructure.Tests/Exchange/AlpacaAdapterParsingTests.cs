namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Tests for the Alpaca v2 REST payload surface (T-198): the assets catalog
/// is a flat array gated by server-served <c>status</c>/<c>tradable</c>
/// metadata, the single-symbol bars endpoint is a flat
/// <c>bars</c>/<c>next_page_token</c> document whose numbers are NUMBERS
/// (never strings), the token pagination is bounded by the shipped page cap,
/// and every request authenticates through the secret-store key pair via the
/// <c>APCA-API-*-KEY</c> headers — a missing pair fails with the keys named.
/// </summary>
public sealed class AlpacaAdapterParsingTests
{
    #region Helpers

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        /// <summary>The last request seen by the stub (endpoint pins).</summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        public int Requests { get; private set; }

        /// <summary>Every request URL the stub saw, in arrival order (the pagination pins).</summary>
        public List<string> SeenUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests++;
            SeenUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubSecretStore : Core.Interfaces.ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string> _pairs;

        public StubSecretStore(IReadOnlyDictionary<string, string>? pairs = null)
        {
            _pairs = pairs ?? new Dictionary<string, string>
            {
                [AlpacaAdapter.ApiKeySecretKey] = "parse-test-key",
                [AlpacaAdapter.ApiSecretSecretKey] = "parse-test-secret",
            };
        }

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_pairs.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static (AlpacaAdapter Adapter, StubHandler Handler) CreateAdapter(
        params string[] payloadsInArrivalOrder) =>
        CreateAdapter(null, payloadsInArrivalOrder);

    private static (AlpacaAdapter Adapter, StubHandler Handler) CreateAdapter(
        IReadOnlyDictionary<string, string>? secretPairs,
        params string[] payloadsInArrivalOrder)
    {
        var queue = new Queue<string>(payloadsInArrivalOrder);
        var handler = new StubHandler(_ => Json(queue.Dequeue()));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var adapter = new AlpacaAdapter(
            NullLogger<AlpacaAdapter>.Instance,
            factory,
            new StubSecretStore(secretPairs),
            TestEndpoints);

        return (adapter, handler);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
        {
            Headers = { ContentType = new("application/json") },
        },
    };

    /// <summary>The local test hosts: the stub intercepts every dial, the URLs pin the record's shape.</summary>
    private static readonly AlpacaEndpoints TestEndpoints = new(
        RestBaseUrl: "http://alpaca-rest.test",
        CatalogRestBaseUrl: "http://alpaca-catalog.test",
        WsUrl: "ws://alpaca-stream.test/stream");

    /// <summary>The bars request every bars leg rides.</summary>
    private static readonly Symbol Aapl = Symbol.Parse("AAPL");

    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    #endregion Helpers

    #region Symbol Catalog

    /// <summary>
    /// Real v2 assets shape: a flat array of asset objects. One malformed
    /// entry (blank symbol), one delisted, one non-tradable — none of them
    /// lands.
    /// </summary>
    private const string CatalogJson =
        """
        [
          {"id":"904897e6-c87d-4f7f-9d05-31a5bbffb9ac","class":"us_equity","exchange":"NASDAQ","symbol":"AAPL","status":"active","tradable":true},
          {"id":"b2b1a5e0-0d47-4c39-9bd1-8dd0bffa1d51","class":"us_equity","exchange":"NASDAQ","symbol":"MSFT","status":"active","tradable":true},
          {"id":"42144a29-9e19-4cb1-8b31-8a2a2e2c1c14","class":"us_equity","exchange":"NASDAQ","symbol":"","status":"active","tradable":true},
          {"id":"6d2c4e8a-1f3b-4c5d-9e2f-7a8b9c0d1e2f","class":"us_equity","exchange":"NYSE","symbol":"DELISTED","status":"inactive","tradable":true},
          {"id":"8a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d","class":"us_equity","exchange":"NYSE","symbol":"NOTRADABLE","status":"active","tradable":false}
        ]
        """;

    [Fact]
    public async Task GetSymbolsAsync_ParsesActiveTradableAssetsAndDropsTheRest()
    {
        var (adapter, _) = CreateAdapter(CatalogJson);

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("AAPL", "MSFT");
        symbols[0].AssetClass.Should().Be(AssetClass.Stock);
        symbols[0].QuoteAsset.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbolsAsync_RequestsTheCatalogOnTheTradingApiHostWithTheKeyHeaders()
    {
        var (adapter, handler) = CreateAdapter(CatalogJson);

        await adapter.GetSymbolsAsync();

        handler.Requests.Should().Be(1);
        var request = handler.LastRequest!;
        request.RequestUri!.Host.Should().Be("alpaca-catalog.test");
        request.RequestUri.AbsolutePath.Should().Be("/v2/assets");
        request.RequestUri.Query.Should().Contain("status=active").And.Contain("asset_class=us_equity");
        request.Headers.GetValues("APCA-API-KEY-ID").Should().ContainSingle().Which.Should().Be("parse-test-key");
        request.Headers.GetValues("APCA-API-SECRET-KEY").Should().ContainSingle().Which.Should().Be("parse-test-secret");
    }

    [Fact]
    public async Task GetSymbolsAsync_NotAnAssetArray_IsAProtocolFailure()
    {
        var (adapter, _) = CreateAdapter("""{"assets":[]}""");

        Func<Task> act = () => adapter.GetSymbolsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not an asset array*");
    }

    [Fact]
    public async Task RestRequests_MissingApiKey_FailsNamingTheSecretStoreKey()
    {
        var (adapter, _) = CreateAdapter(
            new Dictionary<string, string>(),
            CatalogJson);

        Func<Task> act = () => adapter.GetSymbolsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(AlpacaAdapter.ApiKeySecretKey)
            .And.Contain("secret store");
    }

    #endregion Symbol Catalog

    #region Bars

    /// <summary>Real v2 single-symbol bars shape: flat bars array, numbers as numbers, one nanosecond timestamp.</summary>
    private const string BarsOnePageJson =
        """
        {"bars":[
            {"t":"2026-01-02T14:30:00.123456789Z","o":187.1,"h":188.2,"l":186.9,"c":187.5,"v":123456,"n":100,"vw":187.2},
            {"t":"2026-01-02T14:31:00Z","o":187.5,"h":188.0,"l":187.2,"c":187.9,"v":98765,"n":80,"vw":187.6}
        ],"symbol":"AAPL","next_page_token":null}
        """;

    private const string BarsPage1Json =
        """
        {"bars":[{"t":"2026-01-02T14:30:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":10}],"next_page_token":"page-2"}
        """;

    private const string BarsPage2Json =
        """
        {"bars":[{"t":"2026-01-02T14:31:00Z","o":1.5,"h":2.5,"l":1.2,"c":2.2,"v":20}],"next_page_token":null}
        """;

    /// <summary>Build one page that always promises a next one (the hostile-loop probe's payload).</summary>
    private const string BarsNeverEndingPageJson =
        """
        {"bars":[{"t":"2026-01-02T14:30:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":10}],"next_page_token":"next"}
        """;

    [Fact]
    public async Task GetBarsAsync_ParsesNumericRowsAscendingExactDecimals()
    {
        var (adapter, handler) = CreateAdapter(BarsOnePageJson);

        var bars = await adapter.GetBarsAsync(
            Aapl,
            TimeFrame.M1,
            Anchor,
            limit: 10);

        handler.Requests.Should().Be(1, "an exhausted token ends the loop on the first page");
        handler.LastRequest!.RequestUri!.Host.Should().Be("alpaca-rest.test");
        handler.LastRequest.RequestUri.AbsolutePath.Should().Be("/v2/stocks/AAPL/bars");
        handler.LastRequest.RequestUri.Query
            .Should().Contain("timeframe=1Min")
            .And.Contain($"start=2026-01-01T00:00:00Z")
            .And.Contain("&limit=10")
            .And.Contain("feed=iex")
            .And.Contain("sort=asc");

        bars.Should().HaveCount(2);
        bars[0].Time.Should().Be(DateTimeOffset.Parse("2026-01-02T14:30:00.1234567Z"), "the nanosecond fraction trims to DateTimeOffset precision");
        bars[0].Open.Should().Be(187.1m);
        bars[0].High.Should().Be(188.2m);
        bars[0].Low.Should().Be(186.9m);
        bars[0].Close.Should().Be(187.5m);
        bars[0].Volume.Should().Be(123456m);
        bars[1].Time.Should().Be(DateTimeOffset.Parse("2026-01-02T14:31:00Z"));
        bars[1].Close.Should().Be(187.9m);
    }

    [Fact]
    public async Task GetBarsAsync_NextPageToken_RidesTheFollowingRequest()
    {
        var (adapter, handler) = CreateAdapter(BarsPage1Json, BarsPage2Json);

        var bars = await adapter.GetBarsAsync(
            Aapl,
            TimeFrame.M5,
            Anchor,
            limit: 10);

        bars.Select(bar => bar.Time).Should().Equal(
            DateTimeOffset.Parse("2026-01-02T14:30:00Z"),
            DateTimeOffset.Parse("2026-01-02T14:31:00Z"));
        handler.Requests.Should().Be(2);
        handler.SeenUrls[0].Should().NotContain("page_token");
        handler.SeenUrls[1].Should().Contain("page_token=page-2");
    }

    [Fact]
    public async Task GetBarsAsync_NeverEndingNextPageToken_StopsAtTheShippedPageCap()
    {
        var (adapter, handler) = CreateAdapter(
            BarsNeverEndingPageJson,
            BarsNeverEndingPageJson,
            BarsNeverEndingPageJson,
            BarsNeverEndingPageJson,
            BarsNeverEndingPageJson,
            BarsNeverEndingPageJson);

        var bars = await adapter.GetBarsAsync(
            Aapl,
            TimeFrame.M1,
            Anchor,
            limit: 10);

        handler.Requests.Should().Be(5, "the shipped page cap is five pages");
        bars.Should().HaveCount(5, "one bar per promised page, no runaway loop");
    }

    [Theory]
    [InlineData(TimeFrame.M1, "1Min")]
    [InlineData(TimeFrame.M5, "5Min")]
    [InlineData(TimeFrame.M15, "15Min")]
    [InlineData(TimeFrame.M30, "30Min")]
    [InlineData(TimeFrame.H1, "1Hour")]
    [InlineData(TimeFrame.H4, "4Hour")]
    [InlineData(TimeFrame.D1, "1Day")]
    [InlineData(TimeFrame.W1, "1Week")]
    [InlineData(TimeFrame.MN1, "1Month")]
    public async Task GetBarsAsync_MapsTheChartTimeFramesOntoTheV2MultiplierUnitForm(
        TimeFrame timeFrame,
        string expectedTimeframe)
    {
        var (adapter, handler) = CreateAdapter(BarsOnePageJson);

        await adapter.GetBarsAsync(Aapl, timeFrame, Anchor, limit: 10);

        handler.LastRequest!.RequestUri!.Query.Should().Contain($"timeframe={expectedTimeframe}");
    }

    #endregion Bars
}
