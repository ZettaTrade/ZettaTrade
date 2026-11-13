namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// SQLite <see cref="IChartDrawingStore"/> (T-091). One
/// <see cref="ChartDrawingDbContext"/> per operation (cheap: they share
/// the singleton options), so a save never races a load on live state.
/// The initialize step creates the data directory and applies migrations;
/// every read and write awaits the same one-time migration (the startup
/// walk triggers it, but a chart page that loads first waits for it —
/// the annotations read and write only after the schema is real).
/// </summary>
public sealed class SqliteChartDrawingStore(
    DbContextOptions<ChartDrawingDbContext> options,
    ILogger<SqliteChartDrawingStore> logger) : IChartDrawingStore, IAsyncInitializable
{
    /// <summary>Serializes the one-time migration against concurrent first calls.</summary>
    private readonly SemaphoreSlim _ensureReady = new(1, 1);

    /// <summary>Whether the data directory and schema are proven ready.</summary>
    private bool _ready;

    #region Public Methods

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureReadyAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChartDrawing>> LoadAsync(
        string adapterId,
        string symbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChartDrawingDbContext(options);

        var rows = await dbContext.Drawings
            .Where(entry => entry.AdapterId == adapterId
                && entry.Symbol == symbol
                && entry.TimeFrame == timeFrame)
            .ToListAsync(cancellationToken);

        // Assemble one drawing per id, anchors in draw order, ordered by
        // each drawing's first anchor's time (a fresh read paints the same
        // order the trader drew); the identity tie-breaks so the order is
        // deterministic. Anything the stored rows cannot honestly rebuild
        // (an unknown kind name, a degenerate anchor geometry) drops —
        // never a fabricated drawing.
        var groups = rows
            .GroupBy(row => row.DrawingId)
            .Select(group => group
                .OrderBy(row => row.SortOrder)
                .ToArray())
            .OrderBy(group => group[0].Time)
            .ThenBy(group => group[0].DrawingId)
            .ToArray();
        var drawings = new List<ChartDrawing>();
        foreach (var ordered in groups)
        {
            if (!Enum.TryParse<ChartDrawingKind>(ordered[0].Kind, out var kind))
            {
                logger.LogWarning(
                    "Chart drawing {DrawingId} dropped on load: unknown kind name {Kind}",
                    ordered[0].DrawingId,
                    ordered[0].Kind);
                continue;
            }

            var anchors = ordered
                .Select(row => new ChartDrawingAnchor(row.Time, row.Price))
                .ToArray();

            try
            {
                ChartDrawing.Validate(kind, anchors);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(
                    ex,
                    "Chart drawing {DrawingId} dropped on load: degenerate {Kind} geometry",
                    ordered[0].DrawingId,
                    kind);
                continue;
            }

            // The text annotation's string rides the drawing (every anchor
            // row carries it; the first row's value reads back).
            drawings.Add(new ChartDrawing
            {
                Id = ordered[0].DrawingId,
                Kind = kind,
                Anchors = anchors,
                Text = ordered[0].Text,
            });
        }

        return drawings;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChartDrawing>> SaveAsync(
        string adapterId,
        string symbol,
        TimeFrame timeFrame,
        IReadOnlyList<ChartDrawing> drawings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(drawings);

        // Unnamed drawings gain their store identity here: the answer
        // carries named instances so the caller's in-memory layer keeps
        // exactly the identities the store persisted.
        var named = drawings
            .Select(drawing => drawing.Id == Guid.Empty
                ? drawing with { Id = Guid.NewGuid() }
                : drawing)
            .ToArray();
        var count = drawings.Count;

        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChartDrawingDbContext(options);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.Drawings
            .Where(entry => entry.AdapterId == adapterId
                && entry.Symbol == symbol
                && entry.TimeFrame == timeFrame)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var drawing in named)
        {
            for (var i = 0; i < drawing.Anchors.Count; i++)
            {
                dbContext.Drawings.Add(new ChartDrawingEntity
                {
                    DrawingId = drawing.Id,
                    SortOrder = i,
                    AdapterId = adapterId,
                    Symbol = symbol,
                    TimeFrame = timeFrame,
                    Kind = drawing.Kind.ToString(),
                    Time = drawing.Anchors[i].Time,
                    Price = drawing.Anchors[i].Price,
                    Text = drawing.Text,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (named.Length != count)
        {
            logger.LogInformation(
                "Chart drawings layer named {UnnamedCount} fresh drawing(s) for {Symbol} {TimeFrame} on {AdapterId}",
                named.Length - count,
                symbol,
                timeFrame,
                adapterId);
        }

        return named;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The one-time readiness gate: the data directory and the schema are
    /// proven exactly once, no matter which path (the startup walk or the
    /// first chart call) reaches them first.
    /// </summary>
    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _ensureReady.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return;
            }

            await using var dbContext = new ChartDrawingDbContext(options);
            var file = dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The chart drawings database has no connection string.");

            // Self-contained startup: the store must not assume someone else
            // created the data directory (freshly installed on a new machine).
            var directory = System.IO.Path.GetDirectoryName(
                file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                    ? file["Data Source=".Length..]
                    : file);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
            logger.LogInformation(
                "Chart drawings store ready at {DatabaseFile} (migrations applied)",
                file);
            _ready = true;
        }
        finally
        {
            _ensureReady.Release();
        }
    }

    #endregion Private Methods
}
