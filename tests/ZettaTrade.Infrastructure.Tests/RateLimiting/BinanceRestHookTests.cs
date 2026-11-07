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
/// The Binance adapter's REST hook (T-106): every outbound call acquires
/// its budget before the send, the lane classification separates the
/// interactive tail loads from the past-ended background fetches, the
/// kline weight follows the venue's tier schedule by limit, and a 429
/// response with a <c>Retry-After</c> feeds the park guidance back into the
/// limiter before the failure throws.
/// </summary>
public sealed class BinanceRestHookTests
{
    #region Fixture

    /// <summary>The real-shaped klines payload parsed exactly as the adapter did before the limiter.</summary>
    private static readonly string KlinesPayload =
        """[[1698765432000,"35000.01000000","36000.50000000","34000.90000000","35500.25000000","12.34567890"]]""";

    private static Symbol SpotSymbol() => new("BTC", "USDT", AssetClass.Crypto);

    private sealed class StubHandler(
        HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private static (BinanceAdapter Adapter, IRestRateLimiter Limiter) CreateAdapter(
        HttpResponseMessage response)
    {
        var limiter = Substitute.For<IRestRateLimiter>();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new StubHandler(response)));

        return (new BinanceAdapter(NullLogger<BinanceAdapter>.Instance, factory, restLimiter: limiter), limiter);
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
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"code":429}""")),
        };
        if (retryAfter is { } span)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(span);
        }

        return response;
    }

    #endregion

    #region Tests: lane, endpoint and weight pins

    /// <summary>The standard kline load (the 500-bar default is the second tier): user lane, kline key, weight four.</summary>
    [Fact]
    public async Task GetBarsAsync_TailLoad_AcquiresUserLaneAtTheKlineTier()
    {
        var (adapter, limiter) = CreateAdapter(Json(KlinesPayload));

        var bars = await adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        await limiter.Received(1).AcquireAsync(
            RestLane.User,
            "/api/v3/klines",
            4,
            Arg.Any<CancellationToken>());
        bars.Should().ContainSingle();
    }

    /// <summary>A past-ended range (the T-086 gap walk shape) rides the background lane.</summary>
    [Fact]
    public async Task GetBarsAsync_PastEndedRange_AcquiresBackgroundLane()
    {
        var (adapter, limiter) = CreateAdapter(Json(KlinesPayload));

        await adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));

        await limiter.Received(1).AcquireAsync(
            RestLane.Background,
            "/api/v3/klines",
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The symbol catalog load is venue background work (a startup fetch), keyed by its own family.</summary>
    [Fact]
    public async Task GetSymbolsAsync_AcquiresBackgroundLaneAtTheCatalogKey()
    {
        var (adapter, limiter) = CreateAdapter(Json("""[{"symbol":"ETHBTC"}]"""));

        await adapter.GetSymbolsAsync();

        await limiter.Received(1).AcquireAsync(
            RestLane.Background,
            "/api/v3/ticker/price",
            4,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The shipped manifest pins the family split: klines carry 3600 of the venue's 6000-per-minute allowance.</summary>
    [Fact]
    public void ShippedManifest_CarriesTheEndpointSplits()
    {
        var klines = BinanceAdapter.DefaultRestLimits.Endpoints.Single(e => e.EndpointKey == "/api/v3/klines");
        var symbols = BinanceAdapter.DefaultRestLimits.Endpoints.Single(e => e.EndpointKey == "/api/v3/ticker/price");

        klines.BudgetTokens.Should().Be(3600);
        symbols.BudgetTokens.Should().Be(2400);
        (klines.BudgetTokens + symbols.BudgetTokens).Should().Be(6000, "the split sums to the venue's published per-minute allowance");
        klines.RefillInterval.Should().Be(TimeSpan.FromMinutes(1));
        BinanceAdapter.DefaultRestLimits.ScaleKind.Should().Be(RestBudgetScaleKind.WeightPerMinute);
    }

    #endregion

    #region Tests: 429 plumbing

    /// <summary>A 429 with a <c>Retry-After</c> reports the park span to the limiter and throws like the bare send did.</summary>
    [Fact]
    public async Task GetBarsAsync_On429ReportsTheRetryAfterAndThrows()
    {
        var (adapter, limiter) = CreateAdapter(TooMany(TimeSpan.FromSeconds(3)));

        var request = () => adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        (await request.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        limiter.Received(1).ReportBlocked("/api/v3/klines", TimeSpan.FromSeconds(3));
    }

    /// <summary>A 429 without a <c>Retry-After</c> still reports the block; the limiter applies its own default span.</summary>
    [Fact]
    public async Task GetSymbolsAsync_On429WithoutHeaderReportsTheBlock()
    {
        var (adapter, limiter) = CreateAdapter(TooMany(retryAfter: null));

        var request = () => adapter.GetSymbolsAsync();
        await request.Should().ThrowAsync<HttpRequestException>();

        limiter.Received(1).ReportBlocked("/api/v3/ticker/price", null);
    }

    /// <summary>An ordinary failure (500) is not a venue park: the limiter hears nothing.</summary>
    [Fact]
    public async Task GetBarsAsync_OnServerErrorDoesNotReportABlock()
    {
        var (adapter, limiter) = CreateAdapter(new(HttpStatusCode.InternalServerError)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("boom")),
        });

        var request = () => adapter.GetBarsAsync(
            SpotSymbol(),
            TimeFrame.M1,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        await request.Should().ThrowAsync<HttpRequestException>();
        limiter.DidNotReceiveWithAnyArgs().ReportBlocked(null!, null);
    }

    #endregion
}
