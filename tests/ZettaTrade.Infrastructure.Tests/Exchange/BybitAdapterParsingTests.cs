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

        /// <summary>Every request URL the stub saw, in arrival order (the two-category catalog pins).</summary>
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

    /// <summary>
    /// Real linear instruments-info shape (T-197): server-served
    /// <c>contractType</c> gates the PERPETUALS only (the dated linear
    /// futures stay out), <c>settleCoin</c> names the settlement asset, the
    /// same status filter and malformed-entry discipline apply.
    /// </summary>
    private const string LinearCatalogOnePageJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
            {"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","contractType":"LinearPerpetual","status":"Trading"},
            {"symbol":"ETHUSDT","baseCoin":"ETH","quoteCoin":"USDT","settleCoin":"USDT","contractType":"LinearPerpetual","status":"Trading"},
            {"symbol":"BTCUSDT-26SEP25","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","contractType":"LinearFutures","status":"Trading"},
            {"symbol":"CLOSEDPERP","baseCoin":"CLOSED","quoteCoin":"USDT","settleCoin":"USDT","contractType":"LinearPerpetual","status":"Closed"},
            {"symbol":"BROKENPERP","baseCoin":"","quoteCoin":"USDT","settleCoin":"","contractType":"LinearPerpetual","status":"Trading"}
        ],"nextPageCursor":""}}
        """;

    [Fact]
    public async Task GetSymbolsAsync_ParsesServerServedPairsAndFiltersStatus()
    {
        var (adapter, _) = CreateAdapter(CatalogOnePageJson, LinearCatalogOnePageJson);

        var symbols = await adapter.GetSymbolsAsync();

        // Only status == "Trading" pairs land; the malformed entry is dropped,
        // never guessed from the concatenated name. The linear walk joins the
        // PERPETUAL contracts only — the dated futures, the closed one and
        // the malformed perp never land.
        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("BTC/USDT", "ETH/BTC", "BTC/USDT:USDT", "ETH/USDT:USDT");
        symbols[2].SettleAsset.Should().Be("USDT");
        symbols[2].IsPerpetual.Should().BeTrue("the settle coin carries the category");
        symbols[0].IsPerpetual.Should().BeFalse("the spot rows keep their spot shape");
    }

    [Fact]
    public async Task GetSymbolsAsync_RequestsBothCategoriesOnOneCatalogEndpoint()
    {
        var (adapter, handler) = CreateAdapter(CatalogOnePageJson, LinearCatalogOnePageJson);

        await adapter.GetSymbolsAsync();

        // The category rides the query on the SAME endpoint (T-197): the spot
        // walk first, the linear walk second.
        handler.Requests.Should().Be(2);
        handler.SeenUrls[0].Should().Contain("/v5/market/instruments-info").And.Contain("category=spot");
        handler.SeenUrls[1].Should().Contain("/v5/market/instruments-info").And.Contain("category=linear");
    }

    [Fact]
    public async Task GetSymbolsAsync_ExhaustedCursor_StopsWithoutExtraRequests()
    {
        var (adapter, handler) = CreateAdapter(
            CatalogPage("XRPUSDT", "cursor-2"),
            CatalogOnePageJson,
            LinearCatalogOnePageJson);

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("XRP/USDT", "BTC/USDT", "ETH/BTC", "BTC/USDT:USDT", "ETH/USDT:USDT");
        handler.Requests.Should().Be(3, "each category's empty cursor ends its own loop");
    }

    [Fact]
    public async Task GetSymbolsAsync_CursorNeverEnding_StopsAtTheShippedPageCap()
    {
        // Every page keeps promising a next one: the bounded cap protects the
        // cursor loop from a pathological (or hostile) response — per
        // category now (T-197): five spot pages plus five linear ones.
        var (adapter, handler) = CreateAdapter(
            CatalogPage("BTCUSDT", "next"),
            CatalogPage("ETHUSDT", "next"),
            CatalogPage("XRPUSDT", "next"),
            CatalogPage("SOLUSDT", "next"),
            CatalogPage("ADAUSDT", "next"),
            CatalogPage("BTCUSDT", "next"),
            CatalogPage("ETHUSDT", "next"),
            CatalogPage("XRPUSDT", "next"),
            CatalogPage("SOLUSDT", "next"),
            CatalogPage("ADAUSDT", "next"));

        var symbols = await adapter.GetSymbolsAsync();

        // The linear catalog rows carry no contractType, so the perp mapping
        // drops every one of them — the served list stays the spot pairs.
        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal(
                "BTC/USDT",
                "ETH/USDT",
                "XRP/USDT",
                "SOL/USDT",
                "ADA/USDT");
        handler.Requests.Should().Be(10, "the shipped page cap is five per category");
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
        // ascending (Binance's order) — reversed in place. The category rides
        // the symbol's own kind: a spot symbol stays on the spot wire.
        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("category=spot")
            .And.Contain("symbol=BTCUSDT")
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

    [Fact]
    public async Task GetBarsAsync_PerpetualSymbol_RequestsTheLinearCategory()
    {
        var (adapter, handler) = CreateAdapter(KlinePayloadJson);

        await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 2);

        // A perpetual rides the linear wire end to end (T-197): the same
        // kline endpoint, the linear category, the same concatenated
        // exchange-format pair.
        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("category=linear")
            .And.Contain("symbol=BTCUSDT")
            .And.Contain("interval=60");
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

    #region Funding History (T-240)

    /// <summary>Real v5 funding-history shape: newest first, BOTH data fields as strings (the 8-hour cadence's 2026-01-01 events: 16:00Z, 08:00Z, 00:00Z).</summary>
    private const string FundingHistoryPayloadJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
            {"symbol":"BTCUSDT","fundingRate":"0.0001","fundingRateTimestamp":"1767283200000"},
            {"symbol":"BTCUSDT","fundingRate":"-0.00025","fundingRateTimestamp":"1767254400000"},
            {"symbol":"BTCUSDT","fundingRate":"0.000075","fundingRateTimestamp":"1767225600000"}
        ]}}
        """;

    /// <summary>The funding history rides the linear wire on its own endpoint (the pane's capability probe resolves it).</summary>
    [Fact]
    public async Task GetFundingRateHistoryAsync_PerpetualSymbol_RequestsTheLinearCategoryOnTheHistoryEndpoint()
    {
        var (adapter, handler) = CreateAdapter(FundingHistoryPayloadJson);

        await adapter.GetFundingRateHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            limit: 200);

        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("category=linear")
            .And.Contain("symbol=BTCUSDT")
            .And.Contain("startTime=1767225600000")
            .And.Contain("endTime=1767312000000")
            .And.Contain("limit=200");
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v5/market/funding/history");
    }

    /// <summary>The newest-first wire parses into the series contract's ASCENDING order with exact decimals.</summary>
    [Fact]
    public async Task GetFundingRateHistoryAsync_NewestFirstRows_ParseAscendingExactDecimals()
    {
        var (adapter, _) = CreateAdapter(FundingHistoryPayloadJson);

        var points = await adapter.GetFundingRateHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        points.Select(static point => point.Rate)
            .Should().Equal([0.000075m, -0.00025m, 0.0001m], "the wire serves newest first; the series contract is ascending");
        points[0].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000));
        points[2].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1767283200000));
    }

    /// <summary>A spot symbol is a request-contract violation: refused, never guessed into another category.</summary>
    [Fact]
    public async Task GetFundingRateHistoryAsync_SpotSymbol_IsRefused()
    {
        var (adapter, _) = CreateAdapter(FundingHistoryPayloadJson);

        var act = () => adapter.GetFundingRateHistoryAsync(
            Symbol.Parse("BTC/USDT"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>The limit rides the venue's documented page bounds [1..200]: out-of-band values are refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetFundingRateHistoryAsync_OutOfBoundsLimit_IsRefused(int limit)
    {
        var (adapter, _) = CreateAdapter(FundingHistoryPayloadJson);

        var act = () => adapter.GetFundingRateHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: limit);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    #endregion Funding History (T-240)

    #region Open-Interest History (T-241)

    /// <summary>Real v5 open-interest shape: newest first, BOTH data fields as strings (the 5-minute buckets' 2026-01-01 snapshots: 00:10Z, 00:05Z, 00:00Z).</summary>
    private const string OpenInterestPayloadJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
            {"symbol":"BTCUSDT","openInterest":"316131537.00000000","timestamp":"1767226200000"},
            {"symbol":"BTCUSDT","openInterest":"315998871.50000000","timestamp":"1767225900000"},
            {"symbol":"BTCUSDT","openInterest":"314851002.00000000","timestamp":"1767225600000"}
        ]}}
        """;

    /// <summary>The OI history rides the linear wire on its own endpoint with the bar interval's bucket.</summary>
    [Fact]
    public async Task GetOpenInterestHistoryAsync_PerpetualSymbol_RequestsTheLinearCategoryOnItsEndpoint()
    {
        var (adapter, handler) = CreateAdapter(OpenInterestPayloadJson);

        await adapter.GetOpenInterestHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            TimeFrame.M15,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            limit: 200);

        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("category=linear")
            .And.Contain("symbol=BTCUSDT")
            .And.Contain("intervalTime=15min")
            .And.Contain("startTime=1767225600000")
            .And.Contain("endTime=1767312000000")
            .And.Contain("limit=200");
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v5/market/open-interest");
    }

    /// <summary>The chart's bar interval floors onto the venue's served bucket set: finer rides the smallest, the weekly and monthly ride the daily.</summary>
    [Theory]
    [InlineData(TimeFrame.M1, "5min")]
    [InlineData(TimeFrame.M5, "5min")]
    [InlineData(TimeFrame.M15, "15min")]
    [InlineData(TimeFrame.M30, "30min")]
    [InlineData(TimeFrame.H1, "1h")]
    [InlineData(TimeFrame.H4, "4h")]
    [InlineData(TimeFrame.D1, "1d")]
    [InlineData(TimeFrame.W1, "1d")]
    [InlineData(TimeFrame.MN1, "1d")]
    public async Task GetOpenInterestHistoryAsync_BarInterval_FloorsOntoTheServedBuckets(TimeFrame barInterval, string expectedInterval)
    {
        var (adapter, handler) = CreateAdapter(OpenInterestPayloadJson);

        await adapter.GetOpenInterestHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            barInterval,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        handler.LastRequest!.RequestUri!.Query.Should().Contain($"intervalTime={expectedInterval}");
    }

    /// <summary>The newest-first wire parses into the series contract's ASCENDING order with exact decimals.</summary>
    [Fact]
    public async Task GetOpenInterestHistoryAsync_NewestFirstRows_ParseAscendingExactDecimals()
    {
        var (adapter, _) = CreateAdapter(OpenInterestPayloadJson);

        var points = await adapter.GetOpenInterestHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            TimeFrame.M5,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        points.Select(static point => point.Size)
            .Should().Equal([314851002.00000000m, 315998871.50000000m, 316131537.00000000m], "the wire serves newest first; the series contract is ascending");
        points[0].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000));
        points[2].Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1767226200000));
    }

    /// <summary>A spot symbol is a request-contract violation: refused, never guessed into another category.</summary>
    [Fact]
    public async Task GetOpenInterestHistoryAsync_SpotSymbol_IsRefused()
    {
        var (adapter, _) = CreateAdapter(OpenInterestPayloadJson);

        var act = () => adapter.GetOpenInterestHistoryAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.M5,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>The limit rides the venue's documented page bounds [1..200]: out-of-band values are refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetOpenInterestHistoryAsync_OutOfBoundsLimit_IsRefused(int limit)
    {
        var (adapter, _) = CreateAdapter(OpenInterestPayloadJson);

        var act = () => adapter.GetOpenInterestHistoryAsync(
            Symbol.Parse("BTC/USDT:USDT"),
            TimeFrame.M5,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: limit);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    #endregion Open-Interest History (T-241)

    #region Leverage Brackets (T-243)

    /// <summary>Real v5 risk-limit shape (measured 2026-11-16): the symbol-filtered
    /// call serves the contract's tiers ascending by id, every numeric member a
    /// STRING, riskLimitValue the notional cap in the settle currency and the
    /// margin members RATES as decimal fractions. The tiers are a synthetic
    /// three-step BTCUSDT table (150 000 / 500 000 / 2 000 000 USDT).</summary>
    private const string LeverageBracketsPayloadJson =
        """
        {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
            {"id":1,"symbol":"BTCUSDT","riskLimitValue":"150000","maintenanceMargin":"0.005","initialMargin":"0.01","isLowestRisk":1,"maxLeverage":"100.00"},
            {"id":2,"symbol":"BTCUSDT","riskLimitValue":"500000","maintenanceMargin":"0.01","initialMargin":"0.02","isLowestRisk":0,"maxLeverage":"50.00"},
            {"id":3,"symbol":"BTCUSDT","riskLimitValue":"2000000","maintenanceMargin":"0.02","initialMargin":"0.05","isLowestRisk":0,"maxLeverage":"20.00"}
        ]}}
        """;

    /// <summary>The bracket table rides the linear risk-limit endpoint with the category and the wire symbol.</summary>
    [Fact]
    public async Task GetLeverageBracketsAsync_PerpetualSymbol_RequestsTheLinearCategoryOnItsEndpoint()
    {
        var (adapter, handler) = CreateAdapter(LeverageBracketsPayloadJson);

        await adapter.GetLeverageBracketsAsync(Symbol.Parse("BTC/USDT:USDT"));

        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("category=linear")
            .And.Contain("symbol=BTCUSDT");
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v5/market/risk-limit");
    }

    /// <summary>The served tiers normalize ascending by cap with CHAINED floors and the rates as served fractions.</summary>
    [Fact]
    public async Task GetLeverageBracketsAsync_Tiers_ParseAscendingChainedExactDecimals()
    {
        var (adapter, _) = CreateAdapter(LeverageBracketsPayloadJson);

        var brackets = await adapter.GetLeverageBracketsAsync(Symbol.Parse("BTC/USDT:USDT"));

        brackets.Should().HaveCount(3);
        brackets[0].NotionalFloor.Should().Be(0m);
        brackets[0].NotionalCap.Should().Be(150_000m);
        brackets[0].MaxLeverage.Should().Be(100m);
        brackets[0].MaintenanceMarginRate.Should().Be(0.005m);
        brackets[1].NotionalFloor.Should().Be(150_000m, "a tier floors at the previous tier's cap");
        brackets[1].NotionalCap.Should().Be(500_000m);
        brackets[1].MaxLeverage.Should().Be(50m);
        brackets[1].MaintenanceMarginRate.Should().Be(0.01m);
        brackets[2].NotionalFloor.Should().Be(500_000m);
        brackets[2].NotionalCap.Should().Be(2_000_000m);
        brackets[2].MaxLeverage.Should().Be(20m);
        brackets[2].MaintenanceMarginRate.Should().Be(0.02m);
    }

    /// <summary>A spot symbol has no risk table at any venue: the request is refused, never guessed.</summary>
    [Fact]
    public async Task GetLeverageBracketsAsync_SpotSymbol_IsRefused()
    {
        var (adapter, _) = CreateAdapter(LeverageBracketsPayloadJson);

        var act = () => adapter.GetLeverageBracketsAsync(Symbol.Parse("BTC/USDT"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion Leverage Brackets (T-243)
}
