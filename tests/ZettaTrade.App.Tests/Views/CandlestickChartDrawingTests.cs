namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// The chart control's drawings-layer render facts (T-091): a placed
/// drawing leaves its own annotation ink INSIDE the plot (the fallback
/// teal carries the trend line, the level and the fib ladder alike), the
/// empty or fully-dropped layer renders BYTE-FOR-BYTE the plain frame
/// (the drop net catches degenerate anchor sets without a crash), and the
/// removal-restore identity holds — clearing the layer restores the
/// single-series rendering bit-exact. The compare facts' export harness
/// rides again: the control's property system enforces thread affinity,
/// so every fact posts its control work to the pump-owned dispatcher.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class CandlestickChartDrawingTests
{
    /// <summary>The annotations layer's fallback ink: the drawing teal (the plain control carries no theme resource).</summary>
    private static readonly SKColor DrawingInk = new(56, 189, 248);

    /// <summary>Ordered frames for the seeded main series (distinct times and prices).</summary>
    private static Bar MainBar(int index) => new(
        Hour(index),
        100m + index,
        110m + index,
        95m + index,
        105m + index,
        5m + index);

    private static DateTimeOffset Hour(int index) =>
        new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddHours(index);

    /// <summary>Run one synchronous control routine on the pump thread (the control's affinity world).</summary>
    private static async Task<T> OnTheDispatcher<T>(Func<T> controlWork)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                done.SetResult(controlWork());
            }
            catch (Exception fault)
            {
                done.SetException(fault);
            }
        });

        await AvaloniaDispatcherPump.DrainAsync();

        return await done.Task;
    }

    /// <summary>
    /// The teal ink scan inside the plot region: teal pixels inside the
    /// mid band (never the axes nor the top strip) of the decoded frame.
    /// </summary>
    private static int DrawingInkCount(byte[] frame)
    {
        using var probe = SKBitmap.Decode(frame);
        var ink = 0;
        for (var y = 36; y < probe!.Height - 36; y += 1)
        {
            for (var x = 64; x < probe.Width - 64; x += 1)
            {
                var pixel = probe.GetPixel(x, y);
                if (Math.Abs(pixel.Red - DrawingInk.Red) < 40 &&
                    Math.Abs(pixel.Green - DrawingInk.Green) < 40 &&
                    Math.Abs(pixel.Blue - DrawingInk.Blue) < 40)
                {
                    ink++;
                }
            }
        }

        return ink;
    }

    /// <summary>
    /// A three-bar trend line, a mid-price horizontal level and a
    /// four-bar-spanning fib retracement: every kind's ink probes into the
    /// plot, and the removal restores the plain frame byte-for-byte.
    /// </summary>
    [Fact]
    public async Task ThePlacedDrawings_InkTheirGeometry_AndTheRemovalRestoresTheFrameBitExact()
    {
        var (plain, drawn, restored) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>(
                    [MainBar(0), MainBar(1), MainBar(2), MainBar(3), MainBar(4)]),
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var plain = chart.RenderSnapshotPngBytes(null);

            chart.Drawings =
            [
                new ChartDrawing
                {
                    Kind = ChartDrawingKind.TrendLine,
                    Anchors =
                    [
                        new ChartDrawingAnchor(Hour(1), 104m),
                        new ChartDrawingAnchor(Hour(4), 109m),
                    ],
                },
                new ChartDrawing
                {
                    Kind = ChartDrawingKind.HorizontalLevel,
                    Anchors = [new ChartDrawingAnchor(Hour(0), 107m)],
                },
                new ChartDrawing
                {
                    Kind = ChartDrawingKind.FibRetracement,
                    Anchors =
                    [
                        new ChartDrawingAnchor(Hour(0), 100m),
                        new ChartDrawingAnchor(Hour(4), 110m),
                    ],
                },
            ];
            var drawn = chart.RenderSnapshotPngBytes(null);

            chart.Drawings = [];
            var restored = chart.RenderSnapshotPngBytes(null);

            return (plain, drawn, restored);
        });

        DrawingInkCount(drawn).Should().BeGreaterThan(0, "the placed drawings carry their own annotation ink");
        drawn.Should().NotEqual(plain, "the annotations layer changed the frame");
        DrawingInkCount(restored).Should().Be(0, "the emptied layer leaves no stale ink");
        restored.Should().Equal(plain, "removing the drawings restores the single-series rendering bit-exact");
    }

    /// <summary>
    /// The drop net: a degenerate layer (a second corner's time flipped, a
    /// level carrying a second anchor it never placed) renders NOTHING for
    /// the corrupted entries — the frame renders byte-for-byte the plain
    /// one, never a fabricated geometry and never a crash.
    /// </summary>
    [Fact]
    public async Task ACorruptDrawingList_RendersThePlainFrameBitExact()
    {
        var (plain, corrupt) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>(
                    [MainBar(0), MainBar(1), MainBar(2), MainBar(3), MainBar(4)]),
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var plain = chart.RenderSnapshotPngBytes(null);

            chart.Drawings =
            [
                new ChartDrawing
                {
                    Kind = ChartDrawingKind.TrendLine,
                    Anchors =
                    [
                        new ChartDrawingAnchor(Hour(4), 109m),
                        new ChartDrawingAnchor(Hour(1), 104m),
                    ],
                },
                new ChartDrawing
                {
                    Kind = ChartDrawingKind.HorizontalLevel,
                    Anchors =
                    [
                        new ChartDrawingAnchor(Hour(0), 107m),
                        new ChartDrawingAnchor(Hour(0), 108m),
                    ],
                },
            ];
            var corrupt = chart.RenderSnapshotPngBytes(null);

            return (plain, corrupt);
        });

        DrawingInkCount(corrupt).Should().Be(0, "the drop net refuses degenerate geometry silently");
        corrupt.Should().Equal(plain, "a corruption the validation refuses renders exactly the plain frame");
    }

    /// <summary>The frames render byte-deterministically while the annotations layer stands.</summary>
    [Fact]
    public async Task TheDrawnFrames_RenderDeterministically()
    {
        var (first, second) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>(
                    [MainBar(0), MainBar(1), MainBar(2), MainBar(3), MainBar(4)]),
                Drawings =
                [
                    new ChartDrawing
                    {
                        Kind = ChartDrawingKind.TrendLine,
                        Anchors =
                        [
                            new ChartDrawingAnchor(Hour(1), 104m),
                            new ChartDrawingAnchor(Hour(4), 109m),
                        ],
                    },
                ],
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var first = chart.RenderSnapshotPngBytes(null);
            var second = chart.RenderSnapshotPngBytes(null);

            return (first, second);
        });

        second.Should().Equal(first, "a standing layer renders the same bytes every pass");
    }
}
