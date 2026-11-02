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
/// Tests for the Bybit v5 REST payload surface (public, key-less): the
/// catalog is served with server-side <c>baseCoin</c>/<c>quoteCoin</c>
/// metadata and a <c>status</c> filter (no Binance-style quote table), the
/// cursor paging is bounded by the shipped page cap, and klines arrive
/// newest-first with ALL numbers as strings. Every response first passes the
/// <c>retCode</c> gate, where a non-zero code is a protocol failure.
/// </summary>
public sealed class BybitAdapterParsingTests
{
    #region Helpers

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        /// <summary>The last request seen by the stub (endpoint pins).</summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private static (BybitAdapter Adapter, StubHandler Handler) CreateAdapter(
        params string[] payloadsInArrivalOrder)
    {
        var queue = new Queue<string>(payloadsInArrivalOrder);
        var handler = new StubHandler(_ => Json(queue.Dequeue()));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        return (new BybitAdapter(NullLogger<BybitAdapter>.Instance, factory), handler);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
        {
            Headers = { ContentType = new("application/json") },
        },
    };

    #endregion Helpers

    #region Symbol Catalog

    /// <summary>
    /// Real instruments-info shape: server-served <c>baseCoin</c>/<c>quoteCoin</c>
    /// with a <c>status</c> filter, plus one malformed entry (blank quote).
    /// </summary>
    private const string CatalogOnePageJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"spot","list":[
            {"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","status":"Trading"},
            {"symbol":"ETHBTC","baseCoin":"ETH","quoteCoin":"BTC","status":"Trading"},
            {"symbol":"CLOSEDUSDT","baseCoin":"CLOSED","quoteCoin":"USDT","status":"Closed"},
            {"symbol":"BROKENUSDT","baseCoin":"","quoteCoin":"USDT","status":"Trading"}
        ],"nextPageCursor":""}}
        """;

    private const string CatalogPageTemplate =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"spot","list":[
            {"symbol":"@symbol@","baseCoin":"@base@","quoteCoin":"@quote@","status":"Trading"}
        ],"nextPageCursor":"@cursor@"}}
        """;

    /// <summary>Build one cursor page of one traded pair (an arbitrary next-cursor promise).</summary>
    private static string CatalogPage(string pair, string cursor) =>
        CatalogPageTemplate
            .Replace("@symbol@", pair)
            .Replace("@base@", pair[..^4])
            .Replace("@quote@", pair[^4..])
            .Replace("@cursor@", cursor);

    [Fact]
    public async Task GetSymbolsAsync_ParsesServerServedPairsAndFiltersStatus()
    {
        var (adapter, _) = CreateAdapter(CatalogOnePageJson);

        var symbols = await adapter.GetSymbolsAsync();

        // Only status == "Trading" pairs land; the malformed entry is dropped,
        // never guessed from the concatenated name.
        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("BTC/USDT", "ETH/BTC");
    }

    [Fact]
    public async Task GetSymbolsAsync_RequestsTheInstrumentsCatalogSpotEndpoint()
    {
        var (adapter, handler) = CreateAdapter(CatalogOnePageJson);

        await adapter.GetSymbolsAsync();

        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/v5/market/instruments-info");
        handler.LastRequest.RequestUri.Query
            .Should().Contain("category=spot").And.Contain("limit=1000");
    }

    [Fact]
    public async Task GetSymbolsAsync_ExhaustedCursor_StopsWithoutExtraRequests()
    {
        var (adapter, handler) = CreateAdapter(
            CatalogPage("XRPUSDT", "cursor-2"),
            CatalogOnePageJson);

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("XRP/USDT", "BTC/USDT", "ETH/BTC");
        handler.Requests.Should().Be(2, "the second page's empty cursor ends the loop");
    }

    [Fact]
    public async Task GetSymbolsAsync_CursorNeverEnding_StopsAtTheShippedPageCap()
    {
        // Every page keeps promising a next one: the bounded cap protects the
        // cursor loop from a pathological (or hostile) response.
        var (adapter, handler) = CreateAdapter(
            CatalogPage("BTCUSDT", "next"),
            CatalogPage("ETHUSDT", "next"),
            CatalogPage("XRPUSDT", "next"),
            CatalogPage("SOLUSDT", "next"),
            CatalogPage("ADAUSDT", "next"));

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal(
                "BTC/USDT",
                "ETH/USDT",
                "XRP/USDT",
                "SOL/USDT",
                "ADA/USDT");
        handler.Requests.Should().Be(5, "the shipped page cap is five");
    }

    [Fact]
    public async Task GetSymbolsAsync_NonZeroRetCode_IsAProtocolFailure()
    {
        var (adapter, _) = CreateAdapter(
            """{"retCode":10001,"retMsg":"params error","result":{}}""");

        Func<Task> act = () => adapter.GetSymbolsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*retCode=10001*params error*");
    }

    #endregion Symbol Catalog

    #region Klines

    /// <summary>Real Bybit v5 kline shape: rows of [startMs, o, h, l, c, vol, turnover], ALL strings, newest first.</summary>
    private const string KlinePayloadJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"spot","symbol":"BTCUSDT","list":[
            ["1793602800000","10.1","11","9.9","10.5","7","2100"],
            ["1793599200000","9.1","9.9","8.9","9.5","6","1800"]
        ]},"retExtInfo":{},"time":1793601172222}
        """;

    [Fact]
    public async Task GetBarsAsync_WithNewestFirstStringRows_ParsesAscendingExactDecimals()
    {
        var (adapter, handler) = CreateAdapter(KlinePayloadJson);

        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 2);

        // The Bybit answers arrive newest-first; the adapter bar contract is
        // ascending (Binance's order) — reversed in place.
        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("symbol=BTCUSDT")
            .And.Contain("interval=60")
            .And.Contain("&limit=2");
        bars.Should().HaveCount(2);

        bars[0].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793599200000));
        bars[0].Open.Should().Be(9.1m);
        bars[0].High.Should().Be(9.9m);
        bars[0].Low.Should().Be(8.9m);
        bars[0].Close.Should().Be(9.5m);
        bars[0].Volume.Should().Be(6m);

        bars[1].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793602800000));
        bars[1].Open.Should().Be(10.1m);
        bars[1].Close.Should().Be(10.5m);
        bars[1].Volume.Should().Be(7m);
    }

    [Theory]
    [InlineData(TimeFrame.M1, "1")]
    [InlineData(TimeFrame.M5, "5")]
    [InlineData(TimeFrame.M15, "15")]
    [InlineData(TimeFrame.M30, "30")]
    [InlineData(TimeFrame.H1, "60")]
    [InlineData(TimeFrame.H4, "240")]
    [InlineData(TimeFrame.D1, "D")]
    [InlineData(TimeFrame.W1, "W")]
    [InlineData(TimeFrame.MN1, "M")]
    public async Task GetBarsAsync_MapsTheTimeFrameToTheBybitIntervalGrammar(
        TimeFrame timeFrame,
        string expectedInterval)
    {
        var (adapter, handler) = CreateAdapter(KlinePayloadJson);

        await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            timeFrame,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 1);

        handler.LastRequest!.RequestUri!.Query
            .Should().Contain($"interval={expectedInterval}");
    }

    #endregion Klines
}
