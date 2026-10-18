namespace ZettaTrade.Infrastructure.Exchange;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Binance exchange adapter implementation.
/// </summary>
public sealed class BinanceAdapter : ExchangeAdapterBase, IMarketDataAdapter
{
    #region Properties

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
    public BinanceAdapter(ILogger<BinanceAdapter> logger, HttpClient? httpClient = null)
        : base(logger)
    {
        _httpClient = httpClient ?? new HttpClient();
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
                    item[1].GetDecimal(),
                    item[2].GetDecimal(),
                    item[3].GetDecimal(),
                    item[4].GetDecimal(),
                    item[5].GetDecimal()
                ));
            }
        }

        return bars;
    }

    /// <summary>
    /// Subscribe to ticker, order book, and trades for a symbol.
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        var symbolStr = symbol.GetExchangeFormat(Id).ToLower();

        var subscribeMessage = new
        {
            method = "SUBSCRIBE",
            @params = new[]
            {
                $"{symbolStr}@ticker",
                $"{symbolStr}@depth20@100ms",
                $"{symbolStr}@trade"
            },
            id = DateTime.UtcNow.Ticks
        };

        await _wsClient.SendJsonAsync(subscribeMessage, ct);
    }

    /// <summary>
    /// Unsubscribe from all streams for a symbol.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        var symbolStr = symbol.GetExchangeFormat(Id).ToLower();

        var unsubscribeMessage = new
        {
            method = "UNSUBSCRIBE",
            @params = new[]
            {
                $"{symbolStr}@ticker",
                $"{symbolStr}@depth20@100ms",
                $"{symbolStr}@trade"
            },
            id = DateTime.UtcNow.Ticks
        };

        await _wsClient.SendJsonAsync(unsubscribeMessage, ct);
    }

    #endregion

    #region Private Methods

    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (!root.TryGetProperty("stream", out var streamProp) ||
                !root.TryGetProperty("data", out var data))
            {
                return;
            }

            var stream = streamProp.GetString();
            if (string.IsNullOrEmpty(stream))
                return;

            if (stream.EndsWith("@ticker"))
            {
                HandleTickerMessage(data);
            }
            else if (stream.Contains("@depth"))
            {
                HandleOrderBookMessage(data);
            }
            else if (stream.EndsWith("@trade"))
            {
                HandleTradeMessage(data);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling WebSocket message");
        }
    }

    private void HandleTickerMessage(JsonElement data)
    {
        var symbolStr = data.GetProperty("s").GetString()!;
        var symbol = Symbol.Parse(symbolStr);

        var ticker = new Ticker(
            symbol,
            data.GetProperty("c").GetDecimal(),
            data.GetProperty("b").GetDecimal(),
            data.GetProperty("a").GetDecimal(),
            data.GetProperty("v").GetDecimal(),
            data.GetProperty("P").GetDecimal(),
            DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("E").GetInt64())
        );

        PublishTicker(ticker);
    }

    private void HandleOrderBookMessage(JsonElement data)
    {
        var symbolStr = data.GetProperty("s").GetString()!;
        var symbol = Symbol.Parse(symbolStr);

        var bids = new List<PriceLevel>();
        foreach (var bid in data.GetProperty("b").EnumerateArray())
        {
            bids.Add(new PriceLevel(bid[0].GetDecimal(), bid[1].GetDecimal()));
        }

        var asks = new List<PriceLevel>();
        foreach (var ask in data.GetProperty("a").EnumerateArray())
        {
            asks.Add(new PriceLevel(ask[0].GetDecimal(), ask[1].GetDecimal()));
        }

        var orderBook = new OrderBook
        {
            Symbol = symbol,
            Bids = bids,
            Asks = asks,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("E").GetInt64())
        };

        PublishOrderBook(orderBook);
    }

    private void HandleTradeMessage(JsonElement data)
    {
        var symbolStr = data.GetProperty("s").GetString()!;
        var symbol = Symbol.Parse(symbolStr);

        var trade = new MarketTrade
        {
            Id = data.GetProperty("t").GetInt64().ToString(),
            Symbol = symbol,
            Side = data.GetProperty("m").GetBoolean() ? OrderSide.Sell : OrderSide.Buy,
            Price = new Price(data.GetProperty("p").GetDecimal()),
            Quantity = new Quantity(data.GetProperty("q").GetDecimal()),
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

    #region IDisposable

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();
        await _wsClient.DisposeAsync();
        _httpClient.Dispose();
    }

    #endregion
}
