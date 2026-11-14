namespace ZettaTrade.Infrastructure.Export;

using System.Globalization;
using System.Text;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The one CSV writer for every local export surface (orders, fills,
/// candles). Writes the destination file progressively — one row at a time —
/// so a six-figure history never builds the whole text in memory, and it
/// honors the token between rows: cancellation keeps the rows already
/// written and never leaves a torn line.
/// </summary>
/// <remarks>
/// Format contract (CSV is machine-readable, external tools consume it —
/// documented here once, asserted by the export tests):
/// <list type="bullet">
/// <item>Encoding UTF-8 (no BOM), CRLF line endings per RFC 4180.</item>
/// <item>RFC 4180 field quoting: any field carrying a comma, a double quote,
/// a CR or an LF is wrapped in double quotes with embedded quotes doubled.</item>
/// <item>Decimal point invariant regardless of the UI culture: numbers are
/// formatted with <see cref="CultureInfo.InvariantCulture"/> so a ru-RU host
/// still reads <c>0.01</c>, never <c>0,01</c>.</item>
/// <item>Timestamps ISO-8601 UTC with milliseconds and the Z designator
/// (<c>2026-11-06T09:00:00.000Z</c>), converted from whatever offset the
/// record carried.</item>
/// <item>Column headers are stable English and never localized — the UI
/// strings (resx) stay on the actions, the data file stays tool-readable.</item>
/// <item>An empty input produces a header-only file.</item>
/// </list>
/// </remarks>
public sealed class CsvExportService : ICsvExportService
{
    #region Format Constants

    /// <summary>UTF-8 without the BOM: tool parsers stay byte-clean.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Characters forcing RFC 4180 quoting on a field.</summary>
    private static readonly char[] QuoteTriggerChars = [',', '"', '\r', '\n'];

    /// <summary>ISO-8601 UTC timestamp form: milliseconds, Z designator.</summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>File buffer: one page of rows at a time, never the document.</summary>
    private const int StreamBufferSize = 64 * 1024;

    #endregion Format Constants

    #region Header Rows

    /// <summary>Order journal columns: identity, routing, economics, journey.</summary>
    private static readonly string[] OrdersHeader =
    [
        "id",
        "exchange",
        "symbol",
        "side",
        "type",
        "price",
        "quantity",
        "filledQuantity",
        "state",
        "timeInForce",
        "createdAt",
        "updatedAt",
    ];

    /// <summary>
    /// Fill columns mirroring the trade ledger shape: every execution (a
    /// journal order with filled quantity) becomes one row, and the fee
    /// column carries the commission the adapter stamped at execution
    /// (T-080): an absent commission exports as an empty cell — the honest
    /// unknown, never zero-padded.
    /// </summary>
    private static readonly string[] FillsHeader =
    [
        "id",
        "orderId",
        "symbol",
        "side",
        "price",
        "quantity",
        "executedAt",
        "fee",
    ];

    /// <summary>Candle columns: the acceptance's timestamp plus OHLCV.</summary>
    private static readonly string[] CandlesHeader =
    [
        "timestamp",
        "open",
        "high",
        "low",
        "close",
        "volume",
    ];

    /// <summary>
    /// Position columns: the venue's served fields plus the unrealized PnL
    /// in the symbol's quote currency (T-210).
    /// </summary>
    private static readonly string[] PositionsHeader =
    [
        "id",
        "exchange",
        "symbol",
        "side",
        "quantity",
        "entryPrice",
        "currentPrice",
        "openedAt",
        "stopLoss",
        "takeProfit",
        "unrealizedPnl",
    ];

    /// <summary>Realized-trade columns: one closing fill's booked outcome (T-210).</summary>
    private static readonly string[] RealizedTradesHeader =
    [
        "closedAt",
        "exchange",
        "symbol",
        "side",
        "quantity",
        "entryPrice",
        "exitPrice",
        "realizedPnl",
        "quoteCurrency",
    ];

    /// <summary>
    /// The realized-trade columns' T-211 tail: the user annotations join
    /// ONLY when at least one exported row carries them, so the T-210
    /// column shape stays byte-stable for unannotated journals.
    /// </summary>
    private static readonly string[] RealizedTradesMetaHeader = ["note", "tags"];

    /// <summary>Balance snapshot columns: the asset and its served amount (T-210).</summary>
    private static readonly string[] BalancesHeader =
    [
        "asset",
        "amount",
    ];

    #endregion Header Rows

    #region Public API

    /// <inheritdoc/>
    public async Task ExportOrdersAsync(
        string path,
        IReadOnlyList<Order> orders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(orders);

        await WriteDocumentAsync(
            path,
            OrdersHeader,
            orders.Select(OrderToRow),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExportFillsAsync(
        string path,
        IReadOnlyList<Order> orders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(orders);

        await WriteDocumentAsync(
            path,
            FillsHeader,
            orders.Where(HasExecution).Select(OrderToFillRow),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExportCandlesAsync(
        string path,
        IReadOnlyList<Bar> bars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bars);

        await WriteDocumentAsync(
            path,
            CandlesHeader,
            bars.Select(BarToRow),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExportPositionsAsync(
        string path,
        IReadOnlyList<Position> positions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(positions);

        await WriteDocumentAsync(
            path,
            PositionsHeader,
            positions.Select(PositionToRow),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExportRealizedTradesAsync(
        string path,
        IReadOnlyList<StatementRealizedTrade> trades,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(trades);

        // The T-211 columns join only with data present: a journal with no
        // annotations exports byte-identical to the T-210 shape (the header
        // stability the existing consumers pin), while any annotated row
        // grows the note/tags tail — rows without one carry honest empty
        // cells.
        var annotated = trades.Any(trade => trade.Note is not null || trade.Tags is not null);
        var header = annotated
            ? [.. RealizedTradesHeader, .. RealizedTradesMetaHeader]
            : RealizedTradesHeader;

        await WriteDocumentAsync(
            path,
            header,
            trades.Select(trade => RealizedTradeToRow(trade, annotated)),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExportBalancesAsync(
        string path,
        IReadOnlyDictionary<string, Money> balances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(balances);

        await WriteDocumentAsync(
            path,
            BalancesHeader,
            balances.Keys.Order(StringComparer.Ordinal).Select(asset => BalanceToRow(asset, balances[asset])),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IOrderSectionStream> BeginOrderStreamAsync(
        string ordersPath,
        string fillsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ordersPath);
        ArgumentNullException.ThrowIfNull(fillsPath);

        var ordersWriter = await OpenSectionAsync(ordersPath, OrdersHeader, cancellationToken);
        StreamWriter fillsWriter;
        try
        {
            fillsWriter = await OpenSectionAsync(fillsPath, FillsHeader, cancellationToken);
        }
        catch
        {
            // The orders section opened but the fills section failed:
            // release the orders writer honestly before the failure
            // propagates, never a leaked handle on a failed begin.
            await ordersWriter.DisposeAsync();
            throw;
        }

        return new OrderSectionStream(ordersWriter, fillsWriter);
    }

    #endregion Public API

    #region Document Writing

    /// <summary>
    /// Open the destination and write the document row by row. The row
    /// enumeration stays lazy (LINQ over the source list) and every row is
    /// written before the next is built — the peak memory stays one row, so
    /// a hundred-thousand-row journal exports with the same footprint as a
    /// ten-row one.
    /// </summary>
    private static async Task WriteDocumentAsync(
        string path,
        string[] header,
        IEnumerable<string?[]> rows,
        CancellationToken cancellationToken)
    {
        // The destination is a plain local path whose parent must already
        // exist: a picked path under a nonexistent directory fails honestly
        // here (DirectoryNotFoundException) instead of silently raising
        // folders the user never asked for.
        FileInfo info = new(path);
        if (info.Directory is not { Exists: true })
        {
            throw new DirectoryNotFoundException(
                $"The export destination directory does not exist: '{info.Directory?.FullName ?? path}'");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            useAsync: true);
        await using var writer = new StreamWriter(stream, Utf8NoBom);

        await WriteRowAsync(writer, header);

        foreach (var row in rows)
        {
            // The cancellation point between rows: a cancelled token stops
            // before the next row starts, never inside a line.
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(writer, row);
        }

        await writer.FlushAsync();
    }

    /// <summary>
    /// Write one RFC-quoted CSV line. Escaping is applied per field at write
    /// time: commas, quotes and newlines inside a value are contained, the
    /// separator structure of the file stays intact, and every export
    /// re-parses to the same fields it was written from.
    /// </summary>
    private static async Task WriteRowAsync(TextWriter writer, string?[] fields)
    {
        var line = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            AppendField(line, fields[i]);
        }

        await writer.WriteLineAsync(line);
    }

    /// <summary>
    /// Append one field, quoting it when it carries any RFC 4180 trigger
    /// character. An empty field appends as nothing (an empty cell, not a
    /// quoted one).
    /// </summary>
    private static void AppendField(StringBuilder line, string? field)
    {
        if (field is not { Length: > 0 } value)
        {
            return;
        }

        if (value.IndexOfAny(QuoteTriggerChars) >= 0)
        {
            line.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            return;
        }

        line.Append(value);
    }

    #endregion Document Writing

    #region Section Streaming (T-219)

    /// <summary>
    /// Open one statement section: the directory must exist (the same honest
    /// <see cref="DirectoryNotFoundException"/> the one-shot exports raise),
    /// the file opens (overwritten if present) and the header row lands
    /// immediately — the section exists from its first byte and grows with
    /// the scan.
    /// </summary>
    private static async Task<StreamWriter> OpenSectionAsync(
        string path,
        string[] header,
        CancellationToken cancellationToken)
    {
        // The destination is a plain local path whose parent must already
        // exist: a picked path under a nonexistent directory fails honestly
        // here (DirectoryNotFoundException) instead of silently raising
        // folders the user never asked for.
        FileInfo info = new(path);
        if (info.Directory is not { Exists: true })
        {
            throw new DirectoryNotFoundException(
                $"The export destination directory does not exist: '{info.Directory?.FullName ?? path}'");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            useAsync: true);
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(stream, Utf8NoBom);
            await WriteRowAsync(writer, header);
            return writer;
        }
        catch
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
            else
            {
                await stream.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>
    /// One open order-section pair: each appended page writes its orders
    /// rows into the orders file and its fills rows (the orders that carry
    /// an execution) into the fills file — the same mappers the one-shot
    /// exports speak, so the streamed bytes stay identical to theirs.
    /// Disposal flushes and closes both; the rows already written stay on
    /// disk.
    /// </summary>
    private sealed class OrderSectionStream(StreamWriter orders, StreamWriter fills) : IOrderSectionStream
    {
        /// <inheritdoc/>
        public async Task AppendAsync(IReadOnlyList<Order> page, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(page);

            foreach (var order in page)
            {
                // The cancellation point between rows: a cancelled token
                // stops before the next row starts, never inside a line.
                cancellationToken.ThrowIfCancellationRequested();
                await WriteRowAsync(orders, OrderToRow(order));
                if (HasExecution(order))
                {
                    await WriteRowAsync(fills, OrderToFillRow(order));
                }
            }
        }

        /// <summary>Flush and close both sections; the rows already written stay on disk.</summary>
        public async ValueTask DisposeAsync()
        {
            await orders.DisposeAsync();
            await fills.DisposeAsync();
        }
    }

    #endregion Section Streaming (T-219)

    #region Row Mappers

    /// <summary>One journal order as a CSV row (times in UTC, decimals invariant).</summary>
    private static string?[] OrderToRow(Order order) => new string?[]
    {
        order.Id,
        order.ExchangeId,
        order.Symbol.Normalized,
        order.Side.ToString(),
        order.Type.ToString(),
        FormatAmount(order.Price.Value),
        FormatAmount(order.Quantity.Value),
        FormatAmount(order.FilledQuantity.Value),
        order.Status.ToString(),
        order.TimeInForce.ToString(),
        FormatTimestamp(order.CreatedAt),
        order.UpdatedAt is { } updatedAt ? FormatTimestamp(updatedAt) : null,
    };

    /// <summary>
    /// One execution as a trade-ledger row: the fill's identity rides its
    /// journal order (a paper execution fills the whole order), the price is
    /// the execution price the adapter recorded, the quantity is the filled
    /// part, the execution time is the order's last update (the fill time in
    /// the paper ledger) or the placement when no update was written, and
    /// the fee is the recorded commission — an empty cell when the adapter
    /// published none (an honest unknown, never a fabricated zero).
    /// </summary>
    private static string?[] OrderToFillRow(Order order) => new string?[]
    {
        order.Id,
        order.Id,
        order.Symbol.Normalized,
        order.Side.ToString(),
        FormatAmount(order.Price.Value),
        FormatAmount(order.FilledQuantity.Value),
        FormatTimestamp(order.UpdatedAt ?? order.CreatedAt),
        order.Commission is { } fee ? FormatAmount(fee) : null,
    };

    /// <summary>One candle as a CSV row (timestamp UTC, OHLCV invariant).</summary>
    private static string?[] BarToRow(Bar bar) => new string?[]
    {
        FormatTimestamp(bar.Time),
        FormatAmount(bar.Open),
        FormatAmount(bar.High),
        FormatAmount(bar.Low),
        FormatAmount(bar.Close),
        FormatAmount(bar.Volume),
    };

    /// <summary>Whether a journal order carries at least one execution.</summary>
    internal static bool HasExecution(Order order) =>
        order.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled
        && order.FilledQuantity.Value > 0m;

    /// <summary>
    /// One open position as a CSV row: the venue's served fields plus the
    /// unrealized PnL in the symbol's quote currency — an empty cell when
    /// the symbol carries no quote asset (the PnL is undefined without one,
    /// never a fabricated currency).
    /// </summary>
    private static string?[] PositionToRow(Position position)
    {
        var quote = position.Symbol.QuoteAsset;
        return new string?[]
        {
            position.Id,
            position.ExchangeId,
            position.Symbol.Normalized,
            position.Side.ToString(),
            FormatAmount(position.Quantity.Value),
            FormatAmount(position.EntryPrice.Value),
            FormatAmount(position.CurrentPrice.Value),
            FormatTimestamp(position.OpenedAt),
            position.StopLoss is { } stop ? FormatAmount(stop.Value) : null,
            position.TakeProfit is { } take ? FormatAmount(take.Value) : null,
            quote is { } quoteAsset ? FormatAmount(position.UnrealizedPnl(quoteAsset).Amount) : null,
        };
    }

    /// <summary>One booked closing fill's outcome as a CSV row. The T-211
    /// annotation tail rides only when the document carries it
    /// (<paramref name="withMeta"/>): the whole file keeps one column shape.</summary>
    private static string?[] RealizedTradeToRow(StatementRealizedTrade trade, bool withMeta)
    {
        var row = new string?[]
        {
            FormatTimestamp(trade.ClosedAt),
            trade.ExchangeId,
            trade.Symbol,
            trade.ClosingSide,
            FormatAmount(trade.Quantity),
            FormatAmount(trade.EntryPrice),
            FormatAmount(trade.ExitPrice),
            FormatAmount(trade.RealizedPnl),
            trade.QuoteCurrency,
        };

        if (!withMeta)
        {
            return row;
        }

        return
        [
            .. row,
            trade.Note,
            trade.Tags,
        ];
    }

    /// <summary>One served balance as a CSV row (the key names the asset).</summary>
    private static string?[] BalanceToRow(string asset, Money balance) => new string?[]
    {
        asset,
        FormatAmount(balance.Amount),
    };

    #endregion Row Mappers

    #region Field Formatting

    /// <summary>
    /// The invariant decimal form — the machine-readable number spelled the
    /// same on every host culture (the ru-locale decimal trap never fires:
    /// no "0,01" can reach the file).
    /// </summary>
    private static string FormatAmount(decimal amount) =>
        amount.ToString(CultureInfo.InvariantCulture);

    /// <summary>ISO-8601 UTC with the Z designator, offset-independent.</summary>
    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    #endregion Field Formatting
}
