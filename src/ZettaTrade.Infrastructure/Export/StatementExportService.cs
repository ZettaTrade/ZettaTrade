namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The statement bundle writer (T-210), streamed (T-219): the order-side
/// sections (orders.csv, fills.csv) open at BeginAsync and every scanned
/// page appends straight into them — a six-figure journal streams through
/// one page of memory instead of materializing the whole order list before
/// the first byte lands. The realized ledger and the balances join at
/// CompleteAsync (the end-of-scan reads), and the JSON manifest is written
/// LAST so a bundle interrupted mid-write is detectable — a manifest on
/// disk means every section behind it landed whole.
/// </summary>
/// <remarks>
/// Manifest format contract (a stored document, human-inspected and
/// machine-read): camelCase keys, indented, serializer-default ISO
/// timestamps, null filter parts riding as explicit nulls (the honest
/// absent). The section names derive from the manifest's base: a picked
/// <c>trade-statement.json</c> yields <c>trade-statement.orders.csv</c>,
/// <c>trade-statement.fills.csv</c>, <c>trade-statement.realized-pnl.csv</c>
/// and <c>trade-statement.balances.csv</c>.
/// </remarks>
public sealed class StatementExportService(ICsvExportService csv) : IStatementExportService
{
    #region Format Constants

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    #endregion Format Constants

    #region Public Methods

    /// <inheritdoc/>
    public async Task<IStatementExportSession> BeginAsync(
        string manifestPath,
        StatementExportHeader header,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(header);

        var directory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(manifestPath);

        // The order-side sections open first and stay open across the scan:
        // every page appends into both files as it arrives, so the journal
        // never materializes whole.
        var sections = await csv.BeginOrderStreamAsync(
            Sibling(directory, baseName, ".orders.csv"),
            Sibling(directory, baseName, ".fills.csv"),
            cancellationToken);

        return new Session(csv, manifestPath, directory, baseName, header, sections);
    }

    #endregion Public Methods

    #region Section Names

    /// <summary>A section's sibling path: the manifest's base plus the suffix.</summary>
    private static string Sibling(string directory, string baseName, string suffix) =>
        Path.Combine(directory, $"{baseName}{suffix}");

    #endregion Section Names

    #region Session

    /// <summary>
    /// One bundle in flight: the open order-side sections, the counted rows
    /// and the manifest parts remembered from BeginAsync. CompleteAsync
    /// closes the sections, writes the realized ledger and the balances,
    /// then the manifest LAST; abandoning the session (a scan fault, a
    /// cancelled user) disposes the sections — what landed stays on disk,
    /// the manifest never lands.
    /// </summary>
    private sealed class Session(
        ICsvExportService csv,
        string manifestPath,
        string directory,
        string baseName,
        StatementExportHeader header,
        IOrderSectionStream sections) : IStatementExportSession
    {
        private int _orders;
        private int _fills;

        /// <inheritdoc/>
        public async Task AppendOrdersPageAsync(
            IReadOnlyList<Order> page,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(page);

            await sections.AppendAsync(page, cancellationToken);
            _orders += page.Count;
            _fills += page.Count(CsvExportService.HasExecution);
        }

        /// <inheritdoc/>
        public async Task CompleteAsync(
            StatementExportTail tail,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tail);

            // The streamed sections close first: their files are complete
            // documents before the tail writes begin.
            await sections.DisposeAsync();

            // The realized ledger and the balances ride the end-of-scan
            // reads: the accumulator's booked rows and the traded surface's
            // snapshot, written with the same one-shot writers.
            await csv.ExportRealizedTradesAsync(
                Sibling(directory, baseName, ".realized-pnl.csv"),
                tail.RealizedTrades,
                cancellationToken);
            await csv.ExportBalancesAsync(
                Sibling(directory, baseName, ".balances.csv"),
                tail.Balances,
                cancellationToken);

            // The manifest last: its presence on disk marks the bundle whole.
            var manifest = new StatementManifest(
                GeneratedAt: tail.GeneratedAt,
                Filter: new StatementManifestFilter(
                    Symbol: header.SymbolFilter,
                    Exchange: header.ExchangeFilter,
                    FromUtc: header.FromUtc,
                    ToUtc: header.ToUtc,
                    IsPaper: header.IsPaperFilter),
                Counts: new StatementManifestCounts(
                    Orders: _orders,
                    Fills: _fills,
                    RealizedTrades: tail.RealizedTrades.Count,
                    Balances: tail.Balances.Count),
                Files:
                [
                    new StatementManifestFile($"{baseName}.orders.csv", _orders),
                    new StatementManifestFile($"{baseName}.fills.csv", _fills),
                    new StatementManifestFile($"{baseName}.realized-pnl.csv", tail.RealizedTrades.Count),
                    new StatementManifestFile($"{baseName}.balances.csv", tail.Balances.Count),
                ]);

            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, ManifestOptions),
                cancellationToken);
        }

        /// <summary>Dispose the open sections; the rows already written stay
        /// on disk (the manifest-last rule keeps a torn bundle detectable).
        /// A session already completed disposes quietly: the sections are
        /// closed, nothing re-opens.</summary>
        public async ValueTask DisposeAsync() => await sections.DisposeAsync();
    }

    #endregion Session

    #region Manifest Shape

    private sealed record StatementManifest(
        DateTimeOffset GeneratedAt,
        StatementManifestFilter Filter,
        StatementManifestCounts Counts,
        List<StatementManifestFile> Files);

    private sealed record StatementManifestFilter(
        string? Symbol,
        string? Exchange,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        bool? IsPaper);

    private sealed record StatementManifestCounts(
        int Orders,
        int Fills,
        int RealizedTrades,
        int Balances);

    private sealed record StatementManifestFile(string Name, int Rows);

    #endregion Manifest Shape
}
