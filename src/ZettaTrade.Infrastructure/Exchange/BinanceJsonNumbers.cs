namespace ZettaTrade.Infrastructure.Exchange;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// Tolerant numeric reader for Binance payloads. The REST API and the
/// WebSocket streams return numbers and number-strings interchangeably
/// (klines, ticker, depth and trades all carry values like
/// <c>"50000.00000000"</c>), so a raw <see cref="JsonElement.GetDecimal()"/>
/// throws on real data. Never guesses: any other JSON kind is an error,
/// not a defaulted value.
/// </summary>
internal static class BinanceJsonNumbers
{
    /// <summary>Read a decimal from a JSON number or JSON string element.</summary>
    public static decimal ParseDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.GetDecimal();

        if (element.ValueKind == JsonValueKind.String)
            return decimal.Parse(
                element.GetString() ?? string.Empty,
                NumberStyles.Number,
                CultureInfo.InvariantCulture);

        throw new InvalidOperationException(
            $"Expected a numeric value (number or numeric string) but the element has kind {element.ValueKind}.");
    }
}
