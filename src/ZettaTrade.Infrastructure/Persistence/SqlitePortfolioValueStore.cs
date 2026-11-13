namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// SQLite <see cref="IPortfolioValueStore"/>. One
/// <see cref="PortfolioValueDbContext"/> per operation (cheap: they share
/// the singleton options object which only carries the connection string),
/// so writes never share live state. The initialize step creates the data
/// directory and applies migrations.
/// </summary>
public sealed class SqlitePortfolioValueStore(
    DbContextOptions<PortfolioValueDbContext> options,
    ILogger<SqlitePortfolioValueStore> logger) : IPortfolioValueStore, IAsyncInitializable
{
    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var dbContext = new PortfolioValueDbContext(options);
        var file = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The portfolio value database has no connection string.");

        // Self-contained startup: the store must not assume someone else
        // created the data directory (e.g. freshly installed on a new machine).
        var directory = Path.GetDirectoryName(
            file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                ? file["Data Source=".Length..]
                : file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await dbContext.Database.MigrateAsync(ct);
        logger.LogInformation(
            "Portfolio value store ready at {DatabaseFile} (migrations applied)",
            file);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(PortfolioValueSnapshotRow snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var dbContext = new PortfolioValueDbContext(options);
        dbContext.Snapshots.Add(new PortfolioValueEntry
        {
            ExchangeId = snapshot.ExchangeId,
            TimestampUtc = snapshot.TimestampUtc,
            QuoteCurrency = snapshot.QuoteCurrency,
            TotalValue = snapshot.TotalValue,
            AllocationJson = snapshot.AllocationJson,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PortfolioValueSnapshotRow>> GetRecentAsync(
        string exchangeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(exchangeId))
        {
            throw new ArgumentException("The exchange id is required.", nameof(exchangeId));
        }

        // The bounded read states its bound: never below one row, never
        // above the store's batch cap.
        var clamped = Math.Clamp(limit, 1, PortfolioValueStoreLimits.MaxRecentBatch);

        await using var dbContext = new PortfolioValueDbContext(options);
        var entries = await dbContext.Snapshots
            .Where(entry => entry.ExchangeId == exchangeId)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(clamped)
            .ToListAsync(cancellationToken);

        return entries
            .Select(entry => new PortfolioValueSnapshotRow(
                entry.ExchangeId,
                entry.TimestampUtc,
                entry.QuoteCurrency,
                entry.TotalValue,
                entry.AllocationJson))
            .ToArray();
    }

    #endregion Public Methods
}
