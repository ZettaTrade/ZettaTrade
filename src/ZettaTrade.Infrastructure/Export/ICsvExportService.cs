namespace ZettaTrade.Infrastructure.Export;

using ZettaTrade.Core.Entities;

/// <summary>
/// CSV export for the app's local data surfaces: the persisted order journal,
/// the fills extracted from it, and the chart's loaded candle series.
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
}
