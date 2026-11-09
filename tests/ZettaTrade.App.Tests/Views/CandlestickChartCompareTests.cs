namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// The chart control's compare-overlay facts (T-124): the removal-restore
/// identity is proven END TO END through the whole draw pipeline — a
/// single-series frame renders byte-for-byte back after every compare
/// layer is removed (the empty resolve short-circuits the pipeline and no
/// chip strip arms) — a compared layer leaves its own legend-color ink
/// INSIDE the plot even when it is priced far from the main series (the
/// axis union stretches the shared scale, never clipping the neighbor
/// off), the honest-gap layer renders without a crash, and compare frames
/// render byte-deterministically. The export facts' harness rides again:
/// the control's property system enforces thread affinity, so every fact
/// posts its control work to the pump-owned dispatcher.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class CandlestickChartCompareTests
{
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

    private static Bar LayerBar(int index, decimal close) => new(
        Hour(index),
        close - 1m,
        close + 1m,
        close - 2m,
        close,
        3m);

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
    /// One compare layer whose grid shares hour 0..3 exactly, priced far
    /// above the main series — the axis-union invariant's stress shape.
    /// </summary>
    private static ChartCompareLayer FarAboveLayer() => new(
        "ETH/BTC",
        [
            LayerBar(0, 950m),
            LayerBar(1, 960m),
            LayerBar(2, 940m),
            LayerBar(3, 955m),
        ]);

    /// <summary>
    /// The removal-restore identity (T-124): a single-series frame, after
    /// a compared layer rides and falls away, renders BYTE FOR BYTE the
    /// original frame — the empty layer resolve short-circuits the whole
    /// added path and no chip strip arms. Adding the layer provably
    /// changed the bytes first: the identity proves restoration, not
    /// blindness.
    /// </summary>
    [Fact]
    public async Task ARemovedCompareLayer_RestoresThePlainFrameByteForByte()
    {
        var (plain, withCompare, restored) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([MainBar(0), MainBar(1), MainBar(2), MainBar(3)]),
                MainSeriesLabel = "BTC/USDT",
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var plain = chart.RenderSnapshotPngBytes(null);

            chart.CompareLayers = [FarAboveLayer()];
            var withCompare = chart.RenderSnapshotPngBytes(null);

            chart.CompareLayers = [];
            var restored = chart.RenderSnapshotPngBytes(null);

            return (plain, withCompare, restored);
        });

        withCompare.Should().NotEqual(plain, "the layer's polyline and chip strip carry their own ink");
        restored.Should().Equal(plain, "removing the layer restores the single-series rendering bit-exact");
    }

    /// <summary>
    /// A far-priced compared layer draws INSIDE the plot: its legend
    /// color lands in ink well below the chip strip, proving the shared
    /// scale unioned the layer's own axis candidates (a clip-off neighbor
    /// would leave the plot ink-free). The frame renders byte-
    /// deterministically while the layer stands.
    /// </summary>
    [Fact]
    public async Task AFarPricedLayer_DrawsInPlotOnTheUnionedScale_AndRendersDeterministically()
    {
        var (first, second, legendInkCount) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([MainBar(0), MainBar(1), MainBar(2), MainBar(3)]),
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            chart.CompareLayers = [FarAboveLayer()];
            var first = chart.RenderSnapshotPngBytes(null);
            var second = chart.RenderSnapshotPngBytes(null);

            // The first layer slot's legend color (the fallback cyan — no
            // theme resource rides the plain control): the scan rides the
            // plot's UPPER band (a far-above neighbor's home; the bottom
            // half stays the main series' own ink) and stays clear of the
            // chip strip so the layer's POLYLINE ink is what answers.
            var target = ChartCompareSeries.FallbackColors[0];
            using var probe = SKBitmap.Decode(first);
            var legendInkCount = 0;
            for (var y = 40; y < probe!.Height / 2; y += 1)
            {
                for (var x = 100; x < probe.Width - 100; x += 1)
                {
                    var pixel = probe.GetPixel(x, y);
                    if (Math.Abs(pixel.Red - target.Red) < 40 &&
                        Math.Abs(pixel.Green - target.Green) < 40 &&
                        Math.Abs(pixel.Blue - target.Blue) < 40)
                    {
                        legendInkCount++;
                    }
                }
            }

            return (first, second, legendInkCount);
        });

        legendInkCount.Should().BeGreaterThan(
            3,
            "the far-priced polyline drew inside the unioned scale, not clipped off it");
        first.Should().Equal(second, "compare frames render byte-deterministically");
    }

    /// <summary>
    /// The honest gap (T-124): a layer sharing exactly one open time
    /// draws no line (a MoveTo-only path) but arms its chip — the render
    /// completes and differs from the plain frame by the chip strip's
    /// ink alone. A layer the grid never shares is dropped wholesale
    /// (the resolve's all-null rule) and the identity holds.
    /// </summary>
    [Fact]
    public async Task AnUnsharedGrid_DropsTheLayerQuietly_AndASingleSharedSlotRendersWithoutACrash()
    {
        var (plain, oneShared, noneShared) = await OnTheDispatcher(() =>
        {
            var chart = new CandlestickChart
            {
                Bars = new ObservableCollection<Bar>([MainBar(0), MainBar(1), MainBar(2), MainBar(3)]),
                MainSeriesLabel = "BTC/USDT",
            };
            chart.Measure(new Size(320, 200));
            chart.Arrange(new Rect(0, 0, 320, 200));

            var plain = chart.RenderSnapshotPngBytes(null);

            chart.CompareLayers = [new ChartCompareLayer("ETH/BTC", [LayerBar(0, 950m)])];
            var oneShared = chart.RenderSnapshotPngBytes(null);

            chart.CompareLayers = [new ChartCompareLayer("ETH/BTC", [LayerBar(40, 950m), LayerBar(41, 955m)])];
            var noneShared = chart.RenderSnapshotPngBytes(null);

            return (plain, oneShared, noneShared);
        });

        oneShared.Should().NotEqual(plain, "the single shared slot arms the chip strip");
        noneShared.Should().Equal(plain, "a layer sharing no open time is an honest nothing — dropped wholesale");
    }
}
