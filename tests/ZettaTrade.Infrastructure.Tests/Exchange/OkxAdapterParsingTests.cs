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
/// Tests for the OKX v5 REST payload surface (public, key-less): the
/// catalog is served with server-side <c>baseCcy</c>/<c>quoteCcy</c>
/// metadata and a <c>state</c> filter in ONE page (no cursor), the envelope
/// gate reads the STRING <c>code</c> ("0" is success), candles arrive
/// newest-first with ALL numbers as strings and walk back page by page
/// (the venue's 300-row cap), and the wire instId ("BTC-USDT") is mapped
/// adapter-locally — Core's Symbol table stays untouched.
/// </summary>
public sealed class OkxAdapterParsingTests
{
    #region Helpers

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        /// <summary>The last request seen by the stub (endpoint pins).</summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        public List<HttpRequestMessage> RequestsSeen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestsSeen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (OkxAdapter Adapter, StubHandler Handler) CreateAdapter(
        params string[] payloadsInArrivalOrder)
    {
        var queue = new Queue<string>(payloadsInArrivalOrder);
        string? last = null;
        var handler = new StubHandler(_ =>
        {
            // The walk re-serves the last payload once the scripted pages run
            // out: bounded walks (dedup, page cap, `from`) stop on their own,
            // so an exhausted script must never mask the adapter's stopping
            // behavior behind a stub crash.
            if (queue.Count > 0)
            {
                last = queue.Dequeue();
            }

            return Json(last!);
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        return (new OkxAdapter(NullLogger<OkxAdapter>.Instance, factory), handler);
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
    /// Real instruments shape: server-served <c>baseCcy</c>/<c>quoteCcy</c>
    /// with a <c>state</c> filter, plus one suspended entry and one malformed
    /// entry (blank quote).
    /// </summary>
    private const string CatalogJson =
        """
        {"code":"0","msg":"","data":[
            {"instId":"BTC-USDT","instType":"SPOT","baseCcy":"BTC","quoteCcy":"USDT","state":"live"},
            {"instId":"ETHBTC","instType":"SPOT","baseCcy":"ETH","quoteCcy":"BTC","state":"live"},
            {"instId":"SUSPEND-USDT","instType":"SPOT","baseCcy":"SUSPEND","quoteCcy":"USDT","state":"suspend"},
            {"instId":"BROKEN-USDT","instType":"SPOT","baseCcy":"BROKEN","quoteCcy":"","state":"live"}
        ]}
        """;

    [Fact]
    public async Task GetSymbolsAsync_ParsesServerServedPairsAndFiltersState()
    {
        var (adapter, _) = CreateAdapter(CatalogJson);

        var symbols = await adapter.GetSymbolsAsync();

        // Only state == "live" pairs land; the malformed entry is dropped,
        // never guessed from the instId.
        symbols.Select(symbol => symbol.Normalized)
            .Should().Equal("BTC/USDT", "ETH/BTC");
    }

    [Fact]
    public async Task GetSymbolsAsync_RequestsTheInstrumentsCatalogSpotEndpoint()
    {
        var (adapter, handler) = CreateAdapter(CatalogJson);

        await adapter.GetSymbolsAsync();

        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/v5/public/instruments");
        handler.LastRequest.RequestUri.Query
            .Should().Contain("instType=SPOT");
    }

    [Fact]
    public async Task GetSymbolsAsync_NonZeroCode_IsAProtocolFailure()
    {
        var (adapter, _) = CreateAdapter(
            """{"code":"51000","msg":"Instrument ID does not exist","data":[]}""");

        Func<Task> act = () => adapter.GetSymbolsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*code=51000*Instrument ID does not exist*");
    }

    [Fact]
    public async Task GetSymbolsAsync_NumericCodeEnvelope_IsAProtocolFailure()
    {
        // A code that is not the string "0" is a failure even when it parses
        // as zero-like text; an unparsable envelope is never silently served.
        var (adapter, _) = CreateAdapter(
            """{"code":"00","msg":"","data":[{"instId":"X-USDT","baseCcy":"X","quoteCcy":"USDT","state":"live"}]}""");

        Func<Task> act = () => adapter.GetSymbolsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*code=00*");
    }

    #endregion Symbol Catalog

    #region Klines

    /// <summary>
    /// Real OKX v5 candle shape: rows of [ts, o, h, l, c, vol, volCcy,
    /// volCcyQuote, confirm], ALL strings, newest first.
    /// </summary>
    private const string CandlePageJson =
        """
        {"code":"0","msg":"","data":[
            ["1793602800000","10.1","11","9.9","10.5","7","2100","2100","1"],
            ["1793599200000","9.1","9.9","8.9","9.5","6","1800","1800","1"]
        ]}
        """;

    [Fact]
    public async Task GetBarsAsync_WithNewestFirstStringRows_ParsesAscendingExactDecimals()
    {
        var (adapter, handler) = CreateAdapter(CandlePageJson);

        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 2);

        // The OKX answers arrive newest-first; the adapter bar contract is
        // ascending (Binance's order) — reversed in place. The wire instId is
        // the hyphen form, mapped locally.
        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/v5/market/candles");
        handler.LastRequest.RequestUri.Query
            .Should().Contain("instId=BTC-USDT")
            .And.Contain("bar=1H")
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
    public async Task GetBarsAsync_UnconfirmedHeadCandle_IsDropped()
    {
        // The venue marks the forming head candle with confirm="0": it would
        // repaint under the closed history, so it is dropped, never served.
        const string payload =
            """
            {"code":"0","msg":"","data":[
                ["1793602800000","10.1","11","9.9","10.5","7","2100","2100","0"],
                ["1793599200000","9.1","9.9","8.9","9.5","6","1800","1800","1"]
            ]}
            """;
        var (adapter, _) = CreateAdapter(payload);

        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 2);

        bars.Select(bar => bar.Time)
            .Should().Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793599200000));
    }

    [Fact]
    public async Task GetBarsAsync_WideLimit_WalksBackPageByPage()
    {
        // Two pages: the first page's oldest open becomes the second page's
        // `after` cursor; the walk ends when `from` is reached.
        const string pageOne =
            """
            {"code":"0","msg":"","data":[
                ["1793602800000","10.1","11","9.9","10.5","7","2100","2100","1"],
                ["1793599200000","9.1","9.9","8.9","9.5","6","1800","1800","1"]
            ]}
            """;
        const string pageTwo =
            """
            {"code":"0","msg":"","data":[
                ["1793595600000","8.1","8.9","7.9","8.5","5","1500","1500","1"]
            ]}
            """;
        var (adapter, handler) = CreateAdapter(pageOne, pageTwo);

        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 3);

        handler.RequestsSeen.Should().HaveCount(2, "a three-bar window rides two pages");
        handler.RequestsSeen[0].RequestUri!.Query.Should().Contain("&limit=3");
        handler.RequestsSeen[1].RequestUri!.Query.Should().Contain("after=1793599200000");

        bars.Select(bar => bar.Time)
            .Should().Equal(
                DateTimeOffset.FromUnixTimeMilliseconds(1793595600000),
                DateTimeOffset.FromUnixTimeMilliseconds(1793599200000),
                DateTimeOffset.FromUnixTimeMilliseconds(1793602800000));
    }

    [Fact]
    public async Task GetBarsAsync_NeverEndingPages_StopAtTheShippedPageCap()
    {
        // Every page keeps promising older data: the bounded cap protects the
        // walk from a pathological (or hostile) response.
        var (adapter, handler) = CreateAdapter(
            CandlePageJson,
            CandlePageJson,
            CandlePageJson,
            CandlePageJson,
            CandlePageJson);

        // The pages repeat the same opens (the dedup absorbs them) and the
        // cursor never passes `from`: the shipped cap is five pages.
        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            DateTimeOffset.FromUnixTimeMilliseconds(1793590000000),
            limit: 500);

        handler.RequestsSeen.Should().HaveCount(5, "the shipped page cap is five");
        bars.Should().HaveCount(2, "the repeated pages dedup to one page's rows");
    }

    [Fact]
    public async Task GetBarsAsync_OverTheVenuePageSize_ClampsTheRequestPerPage()
    {
        var (adapter, handler) = CreateAdapter(CandlePageJson);

        await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 500);

        // The venue serves at most 300 rows per request; the page ride asks
        // for exactly what remains, capped at the venue page size.
        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("&limit=300");
    }

    [Theory]
    [InlineData(TimeFrame.M1, "1m")]
    [InlineData(TimeFrame.M5, "5m")]
    [InlineData(TimeFrame.M15, "15m")]
    [InlineData(TimeFrame.M30, "30m")]
    [InlineData(TimeFrame.H1, "1H")]
    [InlineData(TimeFrame.H4, "4H")]
    [InlineData(TimeFrame.D1, "1D")]
    [InlineData(TimeFrame.W1, "1W")]
    [InlineData(TimeFrame.MN1, "1M")]
    public async Task GetBarsAsync_MapsTheTimeFrameToTheOkxBarGrammar(
        TimeFrame timeFrame,
        string expectedBar)
    {
        var (adapter, handler) = CreateAdapter(CandlePageJson);

        await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            timeFrame,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 1);

        handler.LastRequest!.RequestUri!.Query
            .Should().Contain($"bar={expectedBar}");
    }

    [Fact]
    public async Task GetBarsAsync_NonPositiveLimit_IsAClientSideError()
    {
        var (adapter, handler) = CreateAdapter(CandlePageJson);

        Func<Task> act = () => adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.H1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 0);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .WithParameterName("limit");
        handler.RequestsSeen.Should().BeEmpty("the guard fires before any request rides");
    }

    #endregion Klines
}
