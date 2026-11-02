namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Tests for Binance payload parsing tolerance: the REST API and the
/// WebSocket streams return numeric fields as JSON strings ("50000.00000000"),
/// which used to crash <see cref="JsonElement.GetDecimal()"/> consumers.
/// The regression runs a real-shaped klines response through the public
/// <see cref="BinanceAdapter.GetBarsAsync"/> path; the paper adapter resolves
/// market-order fill prices from exactly this data.
/// </summary>
public sealed class BinanceAdapterParsingTests
{
    #region Test Constants

    /// <summary>Real Binance klines shape: an array of rows; numbers arrive as strings.</summary>
    private const string KlinesPayload =
        """[[1698765432000,"35000.01000000","36000.50000000","34000.90000000","35500.25000000","12.34567890",1698765432000,"0","0"]]""";

    #endregion Test Constants

    #region Helpers

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        /// <summary>The last request seen by the stub (endpoint pins).</summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static BinanceAdapter CreateAdapter(HttpResponseMessage response)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(new StubHandler(response)));

        return new BinanceAdapter(NullLogger<BinanceAdapter>.Instance, factory);
    }

    private static (BinanceAdapter Adapter, StubHandler Handler) CreateAdapterWithCapture(
        HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        return (new BinanceAdapter(NullLogger<BinanceAdapter>.Instance, factory), handler);
    }

    private static HttpResponseMessage Json(byte[] json) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(json)
        {
            Headers = { ContentType = new("application/json") },
        },
    };

    #endregion Helpers

    #region Public API

    [Fact]
    public async Task GetBarsAsync_WithStringNumbers_ParsesExactDecimals()
    {
        // Arrange: limit=1 keeps the fake payload to a single kline row.
        var adapter = CreateAdapter(Json(Encoding.UTF8.GetBytes(KlinesPayload)));

        // Act
        var bars = await adapter.GetBarsAsync(
            Symbol.Parse("BTC/USDT"),
            TimeFrame.M1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limit: 1);

        // Assert: the string values land in the Bar as exact decimals.
        bars.Should().HaveCount(1);
        var bar = bars[0];
        bar.Time.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1698765432000));
        bar.Open.Should().Be(35000.01000000m);
        bar.High.Should().Be(36000.50000000m);
        bar.Low.Should().Be(34000.90000000m);
        bar.Close.Should().Be(35500.25000000m);
        bar.Volume.Should().Be(12.34567890m);
    }

    #endregion Public API

    #region Symbol Catalog

    /// <summary>Real ticker/price shape: an array of priced symbol rows.</summary>
    private const string TickerPricePayload =
        """[{"symbol":"ETHBTC","price":"0.05"},          {"symbol":"BTCUSDT","price":"50000"},          {"symbol":"USDTTRY","price":"34.2"},          {"symbol":"FDUSDUSDC","price":"0.99"},          {"symbol":"EURUSDC","price":"1.07"},          {"symbol":"BTCUSD1","price":"78000"},          {"symbol":"SUIU","price":"0.9"},          {"symbol":"USDTAED","price":"3.66"}]""";

    [Fact]
    public async Task GetSymbolsAsync_WithPricedSymbolPayload_SplitsEveryKnownQuote()
    {
        var adapter = CreateAdapter(Json(Encoding.UTF8.GetBytes(TickerPricePayload)));

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized).Should().Equal(
            "ETH/BTC", "BTC/USDT", "USDT/TRY", "FDUSD/USDC", "EUR/USDC", "BTC/USD1", "SUI/U", "USDT/AED");
    }

    [Fact]
    public async Task GetSymbolsAsync_RequestsTheCompactPricedSymbolsEndpoint()
    {
        var (adapter, handler) = CreateAdapterWithCapture(
            Json(Encoding.UTF8.GetBytes(TickerPricePayload)));

        await adapter.GetSymbolsAsync();

        handler.LastRequest!.RequestUri!.AbsolutePath
            .Should().Be("/api/v3/ticker/price");
    }

    [Fact]
    public async Task GetSymbolsAsync_WithUnrecognizedQuoteNames_DropsThemCounted()
    {
        // "SUI" alone is a quote literal but not a pair (guard: name must be
        // longer than the quote); "ZZZYYY" matches no known quote at all.
        var payload = """[{"symbol":"BTCUSDT","price":"50000"},{"symbol":"SUI","price":"1"},{"symbol":"ZZZYYY","price":"1"}]""";
        var adapter = CreateAdapter(Json(Encoding.UTF8.GetBytes(payload)));

        var symbols = await adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized).Should().Equal("BTC/USDT");
    }

    #endregion Symbol Catalog

    #region Parser

    [Fact]
    public void ParseDecimal_WithNumericString_ReturnsExactDecimal()
    {
        using var document = JsonDocument.Parse("\"50000.00000000\"");

        BinanceJsonNumbers.ParseDecimal(document.RootElement)
            .Should().Be(50000.00000000m);
    }

    [Fact]
    public void ParseDecimal_WithJsonNumber_ReturnsValue()
    {
        using var document = JsonDocument.Parse("42.5");

        BinanceJsonNumbers.ParseDecimal(document.RootElement)
            .Should().Be(42.5m);
    }

    [Fact]
    public void ParseDecimal_WithNonNumericValues_ThrowsInsteadOfGuessing()
    {
        using var numericString = JsonDocument.Parse("\"not a number\"");
        var fromString = () => BinanceJsonNumbers.ParseDecimal(numericString.RootElement);

        // A non-numeric string is bad data: a parse error, never a default.
        fromString.Should().Throw<FormatException>();

        using var boolean = JsonDocument.Parse("true");
        var fromBoolean = () => BinanceJsonNumbers.ParseDecimal(boolean.RootElement);

        // A value of an unexpected JSON kind is bad data: an explicit error.
        fromBoolean.Should().Throw<InvalidOperationException>();
    }

    #endregion Parser
}
