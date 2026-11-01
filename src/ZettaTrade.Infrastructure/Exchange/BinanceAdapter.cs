namespace ZettaTrade.Infrastructure.Exchange;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Binance exchange adapter implementation (read-only market data).
/// Creates its own WebSocket transport; HTTP clients come from IHttpClientFactory.
/// </summary>
public sealed class BinanceAdapter : ExchangeAdapterBase, IMarketDataAdapter
{
    #region Properties

    /// <summary>Named HttpClientFactory client used for REST requests.</summary>
    public const string HttpClientName = "binance";

    /// <summary>Timeout for REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>Number of levels in the partial book depth snapshot.</summary>
    private const int DepthLevelCount = 20;

    /// <summary>Partial book depth update period in milliseconds.</summary>
    private const int DepthUpdatePeriodMs = 100;

    /// <inheritdoc/>
    public override string Id => "binance";

    /// <inheritdoc/>
    public override string Name => "Binance";

    /// <inheritdoc/>
    public override ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    private readonly HttpClient _httpClient;
    private readonly WebSocketClient _wsClient;
    private readonly string _baseUrl = "https://api.binance.com";
    private readonly string _wsUrl = "wss://stream.binance.com:9443/ws";
    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new BinanceAdapter instance.
    /// </summary>
    public BinanceAdapter(
        ILogger<BinanceAdapter> logger,
        IHttpClientFactory httpClientFactory)
        : base(logger)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _wsClient = new WebSocketClient(_wsUrl, logger);

        _messageSubscription = _wsClient.MessageStream.Subscribe(HandleWebSocketMessage);
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);
    }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await _wsClient.ConnectAsync(ct);
        IsConnected = _wsClient.IsConnected;
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _wsClient.DisconnectAsync();
        IsConnected = false;
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetFromJsonAsync<JsonElement>(
            $"{_baseUrl}/api/v3/exchangeInfo",
            ct);

        var symbols = new List<Symbol>();

        foreach (var item in response.GetProperty("symbols").EnumerateArray())
        {
            var status = item.GetProperty("status").GetString();
            if (status != "TRADING")
                continue;

            var baseAsset = item.GetProperty("baseAsset").GetString()!;
            var quoteAsset = item.GetProperty("quoteAsset").GetString()!;

            symbols.Add(new Symbol(baseAsset, quoteAsset, AssetClass.Crypto));
        }

        return symbols;
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        var interval = ConvertTimeFrame(timeFrame);
        var startTime = from.ToUnixTimeMilliseconds();
        var endTime = to?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var url = $"{_baseUrl}/api/v3/klines" +
                  $"?symbol={symbol.GetExchangeFormat(Id)}" +
                  $"&interval={interval}" +
                  $"&startTime={startTime}" +
                  $"&endTime={endTime}" +
                  $"&limit={limit}";

        var response = await _httpClient.GetFromJsonAsync<JsonElement[]>(url, ct);

        var bars = new List<Bar>();
        if (response != null)
        {
            foreach (var item in response)
            {
                bars.Add(new Bar(
                    DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
                    BinanceJsonNumbers.ParseDecimal(item[1]),
                    BinanceJsonNumbers.ParseDecimal(item[2]),
                    BinanceJsonNumbers.ParseDecimal(item[3]),
                    BinanceJsonNumbers.ParseDecimal(item[4]),
                    BinanceJsonNumbers.ParseDecimal(item[5])
                ));
            }
        }

        return bars;
    }

    /// <summary>
    /// Subscribe to ticker, partial order book, and trades for a symbol.
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _wsClient.SendJsonAsync(
            new SubscriptionRequest(MethodNames.Subscribe, BuildStreamNames(symbol)),
            ct);
    }

    /// <summary>
    /// Unsubscribe from all streams for a symbol.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _wsClient.SendJsonAsync(
            new SubscriptionRequest(MethodNames.Unsubscribe, BuildStreamNames(symbol)),
            ct);
    }

    #endregion

    #region Private Methods

    private string[] BuildStreamNames(Symbol symbol)
    {
        // Partial book depth snapshot; the payload carries "bids"/"asks" (no symbol field).
        var streamSuffixes = new[]
        {
            "ticker",
            $"depth{DepthLevelCount}@{DepthUpdatePeriodMs}ms",
            "trade",
        };

        var symbolStr = symbol.GetExchangeFormat(Id).ToLowerInvariant();
        return streamSuffixes.Select(suffix => $"{symbolStr}@{suffix}").ToArray();
    }

    /// <summary>Extract the symbol part ("btcusdt") from a stream name ("btcusdt@depth20@100ms").</summary>
    private static string GetStreamSymbol(string stream)
    {
        var separatorIndex = stream.IndexOf('@');
        return separatorIndex < 0 ? stream : stream[..separatorIndex];
    }

    private Symbol? ParseExchangeSymbol(string exchangeSymbol)
    {
        return Symbol.TryParseExchangeFormat(exchangeSymbol, Id, out var symbol) && symbol != null
            ? symbol
            : null;
    }

    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            // Combined-stream envelope: {"stream":"btcusdt@ticker","data":{...}}
            if (!root.TryGetProperty("stream", out var streamProp) ||
                !root.TryGetProperty("data", out var data))
            {
                return;
            }

            var stream = streamProp.GetString();
            if (string.IsNullOrEmpty(stream))
                return;

            var symbol = ParseExchangeSymbol(GetStreamSymbol(stream));

            // Unknown symbol format: skip the message rather than guessing a default.
            if (symbol is null)
            {
                Logger.LogWarning("WebSocket stream {Stream}: unrecognised symbol format", stream);
                return;
            }

            if (stream.EndsWith("@ticker"))
            {
                HandleTickerMessage(symbol, data);
            }
            else if (stream.Contains("@depth"))
            {
                HandleOrderBookMessage(symbol, data);
            }
            else if (stream.EndsWith("@trade"))
            {
                HandleTradeMessage(symbol, data);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling WebSocket message");
        }
    }

    private void HandleTickerMessage(Symbol symbol, JsonElement data)
    {
        var ticker = new Ticker(
            symbol,
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("c")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("b")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("a")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("v")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("P")),
            DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("E").GetInt64())
        );

        PublishTicker(ticker);
    }

    private void HandleOrderBookMessage(Symbol symbol, JsonElement data)
    {
        var bids = ParsePriceLevels(data.GetProperty("bids"));
        var asks = ParsePriceLevels(data.GetProperty("asks"));

        // Partial book depth snapshots carry no event timestamp, only lastUpdateId.
        var orderBook = new OrderBook
        {
            Symbol = symbol,
            Bids = bids,
            Asks = asks,
            Timestamp = DateTimeOffset.UtcNow
        };

        PublishOrderBook(orderBook);
    }

    private static List<PriceLevel> ParsePriceLevels(JsonElement array)
    {
        var levels = new List<PriceLevel>();
        foreach (var level in array.EnumerateArray())
        {
            levels.Add(new PriceLevel(
                BinanceJsonNumbers.ParseDecimal(level[0]),
                BinanceJsonNumbers.ParseDecimal(level[1])));
        }

        return levels;
    }

    private void HandleTradeMessage(Symbol symbol, JsonElement data)
    {
        var trade = new MarketTrade
        {
            Id = data.GetProperty("t").GetInt64().ToString(),
            Symbol = symbol,
            // "m" is "is buyer the maker": a maker buy means the aggressor sold.
            Side = data.GetProperty("m").GetBoolean() ? OrderSide.Sell : OrderSide.Buy,
            Price = new Price(BinanceJsonNumbers.ParseDecimal(data.GetProperty("p"))),
            Quantity = new Quantity(BinanceJsonNumbers.ParseDecimal(data.GetProperty("q"))),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("T").GetInt64())
        };

        PublishTrade(trade);
    }

    private void HandleConnectionStateChanged(bool isConnected)
    {
        IsConnected = isConnected;
        Logger.LogInformation("Binance WebSocket connection state: {State}", isConnected ? "Connected" : "Disconnected");
    }

    private static string ConvertTimeFrame(TimeFrame timeFrame)
    {
        return timeFrame switch
        {
            TimeFrame.M1 => "1m",
            TimeFrame.M5 => "5m",
            TimeFrame.M15 => "15m",
            TimeFrame.M30 => "30m",
            TimeFrame.H1 => "1h",
            TimeFrame.H4 => "4h",
            TimeFrame.D1 => "1d",
            TimeFrame.W1 => "1w",
            TimeFrame.MN1 => "1M",
            _ => throw new ArgumentOutOfRangeException(nameof(timeFrame))
        };
    }

    #endregion

    #region IAsyncDisposable

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();
        await _wsClient.DisposeAsync();
        // HttpClient comes from IHttpClientFactory; its lifetime is managed there.
    }

    #endregion

    #region Nested Types

    private sealed record SubscriptionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] string[] Params);

    private static class MethodNames
    {
        public const string Subscribe = "SUBSCRIBE";
        public const string Unsubscribe = "UNSUBSCRIBE";
    }

    #endregion
}
