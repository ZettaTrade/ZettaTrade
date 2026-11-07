namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The deep-history backfill fetcher (T-086): one pass walks the requested
/// coverage window <c>[request.From, request.To)</c> backwards in bounded
/// segments, stitches every fetched page against what it already served (no
/// overlap, no duplicate by open time), persists each raw page through the
/// local candle cache write-through BEFORE it emits, and publishes the
/// segments through a BOUNDED Channel — a slow consumer fills the channel
/// and the producer's own <c>WriteAsync</c> stalls mid-pass, which is the
/// market-data Channel backpressure leg.
/// <para>
/// A depth-limited feed surfaces itself as the final
/// <c>EndOfHistory</c> batch (an empty answer, or a floor strictly above
/// the window's lower edge): no fabricated bars, the caller displays the
/// limit. A cache fault is bookkeeping noise (logged, never a broken pass —
/// the cache is not a source of truth).
/// </para>
/// </summary>
public sealed class CandleBackfillService(
    ICandleCache candleCache,
    ILogger<CandleBackfillService> logger)
{
    /// <summary>Bars requested per fetch segment: inside every adapter's
    /// natural page (<c>GetBarsAsync</c>'s 500-candle default), so each
    /// segment is one adapter request.</summary>
    public const int SegmentSizeBars = 500;

    /// <summary>The bounded leg's capacity: this many segments may stage in
    /// memory while the consumer lags — beyond it the producer waits, which
    /// is the backpressure contract.</summary>
    public const int BackpressureChannelCapacity = 2;

    /// <summary>Segments one pass serves at most (the batching discipline
    /// against the T-106 rate limiter): a pass that exhausts its budget
    /// completes WITHOUT <c>EndOfHistory</c>, and the caller's next trigger
    /// continues the coverage.</summary>
    public const int MaxSegmentsPerPass = 16;

    /// <summary>
    /// Fetch the coverage window as batches of stitched segments. Exactly
    /// one consumer drains the returned sequence (the bounded channel is
    /// <c>SingleReader</c>); a fault, including the request window guard,
    /// surfaces through the enumeration itself. Each yielded
    /// <see cref="CandleBackfillBatch"/> was already persisted through the
    /// cache (write-before-emit).
    /// </summary>
    public async IAsyncEnumerable<CandleBackfillBatch> FetchAsync(
        CandleBackfillRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request.Validate();

        var channel = Channel.CreateBounded<CandleBackfillBatch>(
            new BoundedChannelOptions(BackpressureChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        var producer = ProduceAsync(channel.Writer, request, cancellationToken);

        // The reader is the consumer leg: the enumeration completes when the
        // producer completes the writer and throws exactly the fault the
        // producer completes it with. Every fault inside the producer is
        // carried on the channel, so the producer task itself always settles
        // without an exception.
        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return batch;
        }

        // Normal drain: the writer completed, so the producer settled too.
        await producer;
    }

    /// <summary>
    /// The producer leg: walk the coverage window backwards in segments,
    /// write each raw page through the cache and hand the stitched batch to
    /// the reader. Completion — normal, budget-exhausted or depth-limited —
    /// and every fault ride <c>writer.TryComplete</c>, never the task itself.
    /// </summary>
    private async Task ProduceAsync(
        ChannelWriter<CandleBackfillBatch> writer,
        CandleBackfillRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProduceSegmentsAsync(writer, request, cancellationToken);
            writer.TryComplete();
        }
        catch (Exception error)
        {
            writer.TryComplete(error);
        }
    }

    /// <summary>
    /// The segment walk itself. The cursor starts at the EXCLUSIVE upper
    /// edge and drops to the newest served open after every segment, so the
    /// bands never overlap; a band wider than the feed serves (its floor
    /// sits above the band's lower edge, or it answers empty) ends the pass
    /// with the <c>EndOfHistory</c> batch. Boundary inclusivity differences
    /// between adapters conflate in the band filter and in the caller's
    /// open-time merge — one bar, one open, never a duplicate.
    /// </summary>
    private async Task ProduceSegmentsAsync(
        ChannelWriter<CandleBackfillBatch> writer,
        CandleBackfillRequest request,
        CancellationToken cancellationToken)
    {
        var step = TimeFrameCalendar.NominalBarLength(request.TimeFrame);
        var cursor = request.To;
        var segments = 0;

        while (cursor > request.From && segments < MaxSegmentsPerPass)
        {
            segments++;
            var lower = cursor - (SegmentSizeBars * step) < request.From
                ? request.From
                : cursor - (SegmentSizeBars * step);

            var page = await request.Adapter.GetBarsAsync(
                request.Symbol,
                request.TimeFrame,
                lower,
                to: cursor,
                limit: SegmentSizeBars,
                ct: cancellationToken);

            var bars = page
                .Where(bar => bar.Time >= lower && bar.Time < cursor)
                .OrderBy(bar => bar.Time)
                .ToArray();

            if (bars.Length > 0)
            {
                await WriteThroughSegmentAsync(request, page, cancellationToken);
                await writer.WriteAsync(
                    new CandleBackfillBatch(bars, EndOfHistory: false, DepthFloor: null),
                    cancellationToken);
                cursor = bars[0].Time;
                continue;
            }

            // An empty answer where bars were expected: the feed's series
            // ends here. Nothing fabricated — the pass reports the floor at
            // the cursor: no bar opens exists below it.
            logger.LogInformation(
                "Backfill for {Symbol} {TimeFrame} on {Exchange}: the feed has no bars below {Floor}",
                request.Symbol.Normalized,
                request.TimeFrame,
                request.Adapter.Id,
                cursor);
            await writer.WriteAsync(
                new CandleBackfillBatch([], EndOfHistory: true, DepthFloor: cursor),
                cancellationToken);
            return;
        }

        if (cursor <= request.From)
        {
            return;
        }

        // The segment budget ran out with coverage left: batching discipline
        // (T-106) — the pass ends without a floor claim, the next trigger
        // continues exactly from the partial coverage's lower edge.
        logger.LogInformation(
            "Backfill for {Symbol} {TimeFrame} on {Exchange}: the pass served its {Segments} segments with coverage left below {Edge}",
            request.Symbol.Normalized,
            request.TimeFrame,
            request.Adapter.Id,
            segments,
            cursor);
    }

    /// <summary>
    /// One best-effort write-through of the RAW adapter page: the served set
    /// persists before the batch is emitted (the T-085 discipline — the
    /// next start reads it from the cache, overlap conflates). A cache
    /// fault is bookkeeping noise: logged, never a broken pass.
    /// </summary>
    private async Task WriteThroughSegmentAsync(
        CandleBackfillRequest request,
        IReadOnlyList<Bar> page,
        CancellationToken cancellationToken)
    {
        try
        {
            await candleCache.WriteThroughAsync(
                request.Adapter.Id,
                request.Symbol,
                request.TimeFrame,
                page,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(
                error,
                "Backfill write-through failed for {Symbol} {TimeFrame} on {Exchange}",
                request.Symbol.Normalized,
                request.TimeFrame,
                request.Adapter.Id);
        }
    }
}
