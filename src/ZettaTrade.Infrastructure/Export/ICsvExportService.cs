namespace ZettaTrade.Infrastructure.Export;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// CSV export for the app's local data surfaces: the persisted order journal,
/// the fills extracted from it, the chart's loaded candle series, the traded
/// surface's open positions (T-210), the fill-pair realized-trade ledger and
/// a balances snapshot (the trade statement's sections).
/// </summary>
/// <remarks>
/// Lives in Infrastructure (like the order history store): writing user files
/// is an application service, not a Core contract — Core interfaces stay
/// supervisor-owned and Core entities are reused as the public shape.
/// </remarks>
public interface ICsvExportService
{
    /// <summary>
    /// Export the order journal rows to a CSV file: one row per order, the
    /// journal's persisted fields in stable English columns.
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="orders">The journal rows to export, in export order.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportOrdersAsync(
        string path,
        IReadOnlyList<Order> orders,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export the fills extracted from the journal orders: every order that
    /// carries an execution becomes one ledger-shaped row (the Core
    /// <see cref="Trade"/> shape minus the fee, which the journal persistence
    /// does not record yet — the fee column lands with the fee ledger).
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="orders">The journal rows the fills are extracted from.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportFillsAsync(
        string path,
        IReadOnlyList<Order> orders,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export the chart's loaded candle series to a CSV file: one row per
    /// candle, timestamp plus the OHLCV fields.
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="bars">The loaded bars to export, oldest first.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportCandlesAsync(
        string path,
        IReadOnlyList<Bar> bars,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export the traded surface's open positions (T-210): one row per
    /// position, the venue's served fields plus the unrealized PnL computed
    /// in the symbol's quote currency — an empty cell when the symbol
    /// carries no quote asset (the honest unknown, never a fabricated
    /// currency).
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="positions">The open positions to export, in export order.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportPositionsAsync(
        string path,
        IReadOnlyList<Position> positions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export the fill-pair realized-trade ledger (T-210): one row per
    /// closing fill's outcome, as the trade-stats math booked it.
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="trades">The realized trades to export, in booking order.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportRealizedTradesAsync(
        string path,
        IReadOnlyList<StatementRealizedTrade> trades,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export a balances snapshot (T-210): one row per asset, the asset and
    /// its served amount, asset-ordered for a stable file.
    /// </summary>
    /// <param name="path">Destination file path (overwritten if present).</param>
    /// <param name="balances">The served balances keyed by asset.</param>
    /// <param name="cancellationToken">Cancels the write; rows already
    /// written stay on disk (no partial row is ever produced).</param>
    Task ExportBalancesAsync(
        string path,
        IReadOnlyDictionary<string, Money> balances,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open the statement's two order-side sections as ONE streaming pair
    /// (T-219): the journal-orders file and the fills file both open with
    /// their headers, and each appended page lands in both row by row — a
    /// six-figure journal streams through one page of memory instead of
    /// materializing whole before the first byte. The same row mappers the
    /// one-shot exports speak; the streamed bytes stay identical.
    /// </summary>
    /// <param name="ordersPath">The orders section's destination path
    /// (overwritten if present).</param>
    /// <param name="fillsPath">The fills section's destination path
    /// (overwritten if present).</param>
    /// <param name="cancellationToken">Cancels the open or the header
    /// write; the file already opened stays on disk.</param>
    Task<IOrderSectionStream> BeginOrderStreamAsync(
        string ordersPath,
        string fillsPath,
        CancellationToken cancellationToken = default);
}
