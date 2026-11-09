namespace ZettaTrade.App.Tests.Views;

using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// The chart control's export surface (T-094): the visible slice reads the
/// pan window, and the PNG snapshot renders the CURRENT frame through the
/// same draw operation the screen uses — byte-deterministic for the same
/// seed, a real PNG at the supersampled size, non-blank, and the watermark
/// leaves its own ink (a plain render and a marked render differ). The
/// control's property system enforces thread affinity, so every fact posts
/// its control work to the pump-owned dispatcher (the T-138 world: the
/// pump owns the process-wide dispatcher, a drain runs the posted work)
/// and completes a task-completion source from INSIDE the job — no
/// dispatcher task semantics ride the facts.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class CandlestickChartExportTests
{
    /// <summary>Ordered OHLCV frames for the seeded facts: distinct times,
    /// prices and volumes, so every drawn element has its own geometry.</summary>
    private static Bar Bar(int index) => new(
        new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddHours(index),
        100m + index,
        110m + index,
        95m + index,
        105m + index,
        5m + index);

    private static string TempPngPath() =>
        Path.Combine(Path.GetTempPath(), "zettatrade-chart-export-" + Guid.NewGuid().ToString("N") + ".png");

    /// <summary>Run one synchronous control routine on the pump thread (the
    /// control's affinity world) and surface its result or fault to the fact.</summary>
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
    /// The visible slice of a series smaller than the default window is the
    /// whole series, oldest first — the same copy the render pass consumes.
    /// </summary>
    [Fact]
    public async Task VisibleBarsSlice_OfATinySeries_IsTheWholeSeriesOldestFirst()
    {
        IReadOnlyList<Bar> slice = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([Bar(0), Bar(1), Bar(2)]),
            };

            return chart.VisibleBarsSlice;
        });

        slice.Should().HaveCount(3, "the default window exceeds a tiny series, so every bar is visible");
        slice[0].Time.Should().BeBefore(slice[1].Time);
        slice[1].Time.Should().BeBefore(slice[2].Time);
    }

    /// <summary>An empty series yields an empty slice (an export of nothing exports nothing).</summary>
    [Fact]
    public async Task VisibleBarsSlice_OfAnEmptySeries_IsEmpty()
    {
        var slice = await OnTheDispatcher(() => new CandlestickChart().VisibleBarsSlice);

        slice.Should().BeEmpty("nothing loaded — nothing visible");
    }

    /// <summary>
    /// The PNG snapshot: a real PNG at the supersampled size, byte-
    /// deterministic for the same seed, non-blank — and the watermark leaves
    /// its own ink (a marked render differs from a plain one).
    /// </summary>
    [Fact]
    public async Task RenderSnapshotPng_IsDeterministicNonBlank_AndCarriesTheWatermarkInk()
    {
        var (plain, marked, sliceCount, distinctTotal) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([Bar(0), Bar(1), Bar(2), Bar(3)]),
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var payload = chart.RenderSnapshotPngBytes(null);
            var markedPayload = chart.RenderSnapshotPngBytes("BTC/USDT · H1");

            using var probe = SKBitmap.Decode(payload);
            var colors = new HashSet<SKColor>();
            for (var x = 0; x < probe!.Width; x += 3)
            {
                for (var y = 0; y < probe.Height; y += 3)
                {
                    colors.Add(probe.GetPixel(x, y));
                }
            }

            return (payload, markedPayload, chart.VisibleBarsSlice.Count, colors.Count);
        });

        sliceCount.Should().Be(4, "the seeded bars must reach the snapshot");
        distinctTotal.Should().BeGreaterThan(10, "the seeded frame draws candles, grid and labels over the background");
        plain.Should().NotEqual(marked, "the watermark stamps its own ink over the corner");

        // The PNG signature (the file header, not a re-encoded stream).
        plain.Length.Should().BeGreaterThan(8);
        plain[0].Should().Be(0x89);
        plain[1].Should().Be((byte)'P');
        plain[2].Should().Be((byte)'N');
        plain[3].Should().Be((byte)'G');

        using var bitmap = SKBitmap.Decode(plain);
        bitmap.Should().NotBeNull("the export must decode as an image");

        // 320×200 logical frame × the fixed 2× supersampling scale.
        bitmap.Width.Should().Be(640);
        bitmap.Height.Should().Be(400);
    }

    /// <summary>The render is byte-deterministic: the same seed renders the same frame to the same bytes (the pixel-diff smoke).</summary>
    [Fact]
    public async Task RenderSnapshotPng_IsByteDeterministicForTheSameSeed()
    {
        var (first, second) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([Bar(0), Bar(1), Bar(2), Bar(3)]),
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            return (chart.RenderSnapshotPngBytes(null), chart.RenderSnapshotPngBytes(null));
        });

        first.Should().Equal(second, "the same seed renders the same frame to the same bytes");
    }

    /// <summary>The public export refuses a blank destination with its own fault, before any render runs — the guard is pure (no control state), so it rides the fact thread directly.</summary>
    [Fact]
    public async Task ExportSnapshotPng_WithoutADestination_ThrowsBeforeRendering()
    {
        var chart = new CandlestickChart();

        await FluentActions.Invoking(async () => await chart.ExportSnapshotPngAsync("   ", null))
            .Should().ThrowAsync<ArgumentException>("the guard is the method's first decision and needs no control state");
    }
}
