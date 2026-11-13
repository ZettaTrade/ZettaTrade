namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the SQLite portfolio-value store (T-209): the migration pins,
/// the round-trip fidelity of a snapshot row (the allocation document rides
/// byte-exact), the newest-first bounded read per account, the account
/// isolation, the restart story (a fresh store instance reads another
/// store's rows from the same file), the duplicate-key honesty (a true
/// duplicate timestamp is an error, never a silent overwrite) and the
/// "no secrets" schema white-list.
/// </summary>
public sealed class SqlitePortfolioValueStoreTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static async Task WithTempDatabaseAsync(
        Func<string, SqlitePortfolioValueStore, Task> testBody)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-portfolio-value-{Guid.NewGuid():N}.db");
        try
        {
            await testBody(file, CreateStore(file));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static SqlitePortfolioValueStore CreateStore(string databaseFile) => new(
        new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PortfolioValueDbContext>()
            // Pooling=false: after each context disposes, its SQLite
            // connection closes, so the temp database file is unlocked.
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options,
        NullLogger<SqlitePortfolioValueStore>.Instance);

    private static PortfolioValueSnapshotRow BuildRow(
        string exchangeId = "paper",
        DateTimeOffset? timestamp = null,
        string quote = "USDT",
        decimal total = 1000m,
        string? allocationJson = null) => new(
        exchangeId,
        timestamp ?? BaseTime,
        quote,
        total,
        allocationJson ??
        PortfolioValueAllocationEntry.SerializeAllocation(
        [
            new PortfolioValueAllocationEntry("USDT", total, 1m, total, Priced: true),
        ]));

    #endregion Helpers

    #region Facts

    /// <summary>The initialize applies the migration: the table exists with its composite key.</summary>
    [Fact]
    public async Task Initialize_TheMigrationBuildsTheCompositeKeyedTable()
    {
        await WithTempDatabaseAsync(async (file, store) =>
        {
            // Act
            await store.InitializeAsync();

            // Assert: the migration landed (the table is in the schema).
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=false");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='PortfolioValue'";
            var exists = await command.ExecuteScalarAsync() is not null;
            exists.Should().BeTrue("the InitialPortfolioValue migration built the table");
        });
    }

    /// <summary>A written row reads back field-exact, allocation document included.</summary>
    [Fact]
    public async Task WriteThenRead_TheRowRoundTripsFieldExact()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            // Arrange
            await store.InitializeAsync();
            var allocation = PortfolioValueAllocationEntry.SerializeAllocation(
            [
                new PortfolioValueAllocationEntry("BTC", 0.01m, 50_000m, 500m, Priced: true),
                new PortfolioValueAllocationEntry("USDT", 600m, 1m, 600m, Priced: true),
            ]);
            var row = BuildRow(total: 1100m, allocationJson: allocation);

            // Act
            await store.WriteAsync(row);
            var read = await store.GetRecentAsync("paper", 10);

            // Assert
            read.Should().HaveCount(1);
            var entry = read[0];
            entry.ExchangeId.Should().Be(row.ExchangeId);
            entry.TimestampUtc.Should().Be(row.TimestampUtc);
            entry.QuoteCurrency.Should().Be(row.QuoteCurrency);
            entry.TotalValue.Should().Be(row.TotalValue);
            entry.AllocationJson.Should().Be(row.AllocationJson, "the allocation document rides byte-exact");
        });
    }

    /// <summary>The read is newest-first and the limit clamps into the bounded window.</summary>
    [Fact]
    public async Task GetRecent_IsNewestFirst_AndClampsTheLimit()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            // Arrange
            await store.InitializeAsync();
            foreach (var minute in Enumerable.Range(0, 5))
            {
                await store.WriteAsync(BuildRow(timestamp: BaseTime.AddMinutes(minute), total: 1000m + minute));
            }

            // Act + Assert: the newest row first.
            var newestFirst = await store.GetRecentAsync("paper", 3);
            newestFirst.Select(row => row.TimestampUtc).Should().BeInDescendingOrder();
            newestFirst[0].TimestampUtc.Should().Be(BaseTime.AddMinutes(4));

            // The bound clamps: 0 stays 1, 99999 stays at the store's cap.
            var clampedLow = await store.GetRecentAsync("paper", 0);
            clampedLow.Should().HaveCount(1);
            var clampedHigh = await store.GetRecentAsync("paper", 99999);
            clampedHigh.Should().HaveCount(5, "the account carries fewer rows than the cap");
        });
    }

    /// <summary>Accounts are isolated: another account's rows never leak into one account's read.</summary>
    [Fact]
    public async Task GetRecent_AccountsAreIsolated()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            // Arrange
            await store.InitializeAsync();
            await store.WriteAsync(BuildRow(exchangeId: "paper"));
            await store.WriteAsync(BuildRow(exchangeId: "bybit-testnet", total: 250m));

            // Act + Assert
            (await store.GetRecentAsync("paper", 10)).Should().ContainSingle().Which.TotalValue.Should().Be(1000m);
            (await store.GetRecentAsync("bybit-testnet", 10)).Should().ContainSingle().Which.TotalValue.Should().Be(250m);
        });
    }

    /// <summary>The restart story: a fresh store instance reads another store's rows from the same file.</summary>
    [Fact]
    public async Task GetRecent_AFreshStoreReadsAnotherStoresRows()
    {
        await WithTempDatabaseAsync(async (file, store) =>
        {
            // Arrange
            await store.InitializeAsync();
            await store.WriteAsync(BuildRow());

            // Act
            var fresh = CreateStore(file);
            await fresh.InitializeAsync();
            var read = await fresh.GetRecentAsync("paper", 10);

            // Assert
            read.Should().ContainSingle().Which.TotalValue.Should().Be(1000m);
        });
    }

    /// <summary>A duplicate (account, timestamp) write is an honest error, never a silent overwrite.</summary>
    [Fact]
    public async Task Write_TheSameTimestampTwice_IsAnError()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            // Arrange
            await store.InitializeAsync();
            await store.WriteAsync(BuildRow());

            // Act + Assert
            var write = async () => await store.WriteAsync(BuildRow());
            await write.Should().ThrowAsync<DbUpdateException>("the composite key rejects the duplicate");
        });
    }

    /// <summary>The empty account reads back an empty list.</summary>
    [Fact]
    public async Task GetRecent_AnUnknownAccountReadsBackEmpty()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            (await store.GetRecentAsync("nobody", 10)).Should().BeEmpty();
        });
    }

    /// <summary>The schema carries no secret-named columns (the store family's white-list pin).</summary>
    [Fact]
    public async Task TheSchema_CarriesNoSecretNamedColumns()
    {
        await WithTempDatabaseAsync(async (file, store) =>
        {
            // Arrange
            await store.InitializeAsync();

            // Act + Assert: every column name is accounted for.
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=false");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('PortfolioValue')";
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            names.Should().OnlyContain(name =>
                name.Equals("ExchangeId", StringComparison.Ordinal) ||
                name.Equals("TimestampUtc", StringComparison.Ordinal) ||
                name.Equals("QuoteCurrency", StringComparison.Ordinal) ||
                name.Equals("TotalValue", StringComparison.Ordinal) ||
                name.Equals("AllocationJson", StringComparison.Ordinal),
                "the schema grows only with the accounting record and never with secret-shaped columns");
        });
    }

    #endregion Facts
}
