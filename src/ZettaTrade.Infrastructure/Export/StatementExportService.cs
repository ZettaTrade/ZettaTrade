namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The statement bundle writer (T-210): the four CSV sections ride the ONE
/// T-084 CSV writer (never a re-implementation), and the JSON manifest is
/// written LAST so a bundle interrupted mid-write is detectable — a
/// manifest on disk means every section behind it landed whole.
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
    public async Task WriteAsync(
        string manifestPath,
        StatementExport statement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(statement);

        var directory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(manifestPath);
        string Sibling(string suffix) => Path.Combine(directory, $"{baseName}{suffix}");

        await csv.ExportOrdersAsync(Sibling(".orders.csv"), statement.Orders, cancellationToken);
        await csv.ExportFillsAsync(Sibling(".fills.csv"), statement.Orders, cancellationToken);
        await csv.ExportRealizedTradesAsync(Sibling(".realized-pnl.csv"), statement.RealizedTrades, cancellationToken);
        await csv.ExportBalancesAsync(Sibling(".balances.csv"), statement.Balances, cancellationToken);

        // The manifest last: its presence on disk marks the bundle whole.
        var fillCount = statement.Orders.Count(CsvExportService.HasExecution);
        var manifest = new StatementManifest(
            GeneratedAt: statement.GeneratedAt,
            Filter: new StatementManifestFilter(
                Symbol: statement.SymbolFilter,
                Exchange: statement.ExchangeFilter,
                FromUtc: statement.FromUtc,
                ToUtc: statement.ToUtc,
                IsPaper: statement.IsPaperFilter),
            Counts: new StatementManifestCounts(
                Orders: statement.Orders.Count,
                Fills: fillCount,
                RealizedTrades: statement.RealizedTrades.Count,
                Balances: statement.Balances.Count),
            Files:
            [
                new StatementManifestFile($"{baseName}.orders.csv", statement.Orders.Count),
                new StatementManifestFile($"{baseName}.fills.csv", fillCount),
                new StatementManifestFile($"{baseName}.realized-pnl.csv", statement.RealizedTrades.Count),
                new StatementManifestFile($"{baseName}.balances.csv", statement.Balances.Count),
            ]);

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestOptions),
            cancellationToken);
    }

    #endregion Public Methods

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
