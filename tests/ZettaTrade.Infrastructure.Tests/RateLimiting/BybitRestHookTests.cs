namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The Bybit adapter's REST hook (T-106): the v5 catalog pages and kline
/// segments each acquire their per-endpoint bucket (count-dialect, one
/// request per bucket token), the lane classification matches the Binance
/// rule, and a 429 response feeds the park guidance back. The retCode-wrap
/// stays: a 429 surfaces as an <see cref="HttpRequestException"/> after the
/// limiter has parked, exactly as any transport failure did before.
/// </summary>
public sealed class BybitRestHookTests
{
    #region Fixture

    /// <summary>A real-shaped v5 kline payload: the {"retCode":0,"result":{...}} wrap, newest first.</summary>
    private static readonly string KlinePayload =
        """{"retCode":0,"retMsg":"OK","result":{"list":[[1698765432000,"34000.1","36000.2","35000.1","35203.4","13.01"]],"nextPageCursor":""}}""";

    /// <summary>A real-shaped (if minimal) v5 catalog page.</summary>
    private static readonly string CatalogPayload =
        """{"retCode":0,"retMsg":"OK","result":{"list":[{"baseCoin":"ETH","quoteCoin":"BTC","status":"Trading"}],"nextPageCursor":""}}""";

    private static Symbol SpotSymbol() => new("BTC", "USDT", AssetClass.Crypto);

    /// <summary>
    /// The stub serves a FRESH response per request (T-197): the catalog now
    /// walks BOTH categories on one call, and an HttpResponseMessage — once
    /// sent — is spent, so a shared instance would poison the second send.
    /// </summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private static (BybitAdapter Adapter, IRestRateLimiter Limiter) CreateAdapter(
        Func<HttpResponseMessage> respond)
    {
        var limiter = Substitute.For<IRestRateLimiter>();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new StubHandler(respond)));

        return (new BybitAdapter(NullLogger<BybitAdapter>.Instance, factory, restLimiter: limiter), limiter);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
        {
            Headers = { ContentType = new("application/json") },
        },
    };

    private static HttpResponseMessage TooMany(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"retCode":10002}""")),
        };
        if (retryAfter is { } span)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(span);
        }

        return response;
    }

    #endregion

    #region Tests: lane, endpoint and budget pins

    /// <summary>The standard kline load rides the user lane, one request per bucket token.</summary>
    [Fact]
    public async Task GetBarsAsync_TailLoad_AcquiresUserLaneAtTheKlineBucket()
    {
        var (adapter, limiter) = CreateAdapter(() => Json(KlinePayload));

        var bars = await adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        await limiter.Received(1).AcquireAsync(
            RestLane.User,
            "/v5/market/kline",
            1,
            Arg.Any<CancellationToken>());
        bars.Should().ContainSingle();
    }

    /// <summary>The catalog's two-category walk acquires one token per page request (background work): both categories' pages ride the same endpoint bucket (T-197).</summary>
    [Fact]
    public async Task GetSymbolsAsync_AcquiresPerCatalogPageRequest()
    {
        var (adapter, limiter) = CreateAdapter(() => Json(CatalogPayload));

        await adapter.GetSymbolsAsync();

        await limiter.Received(2).AcquireAsync(
            RestLane.Background,
            "/v5/market/instruments-info",
            1,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The shipped manifest: a per-second request dial (ten per second, per family, as shipped).</summary>
    [Fact]
    public void ShippedManifest_CarriesThePerSecondDishes()
    {
        var families = BybitAdapter.DefaultRestLimits.Endpoints;

        families.Should().HaveCount(4);
        families.Should().OnlyContain(e => e.BudgetTokens == 10 && e.RefillInterval == TimeSpan.FromSeconds(1));
        BybitAdapter.DefaultRestLimits.ScaleKind.Should().Be(RestBudgetScaleKind.RequestsPerSecond);
        families.Select(e => e.EndpointKey).Should().Equal(
            "/v5/market/kline",
            "/v5/market/instruments-info",
            "/v5/market/funding/history",
            "/v5/market/open-interest");
    }

    #endregion

    #region Tests: 429 plumbing

    /// <summary>A 429 with a <c>Retry-After</c> parks the kline bucket and throws the transport failure.</summary>
    [Fact]
    public async Task GetBarsAsync_On429ReportsTheRetryAfterAndThrows()
    {
        var (adapter, limiter) = CreateAdapter(() => TooMany(TimeSpan.FromSeconds(2)));

        var request = () => adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        (await request.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        limiter.Received(1).ReportBlocked("/v5/market/kline", TimeSpan.FromSeconds(2));
    }

    /// <summary>A 429 without a <c>Retry-After</c> still reports the block (the limiter's default span applies).</summary>
    [Fact]
    public async Task GetSymbolsAsync_On429WithoutHeaderReportsTheBlock()
    {
        var (adapter, limiter) = CreateAdapter(() => TooMany(retryAfter: null));

        await FluentActions.Awaiting(() => adapter.GetSymbolsAsync()).Should().ThrowAsync<HttpRequestException>();

        limiter.Received(1).ReportBlocked("/v5/market/instruments-info", null);
    }

    #endregion
}
