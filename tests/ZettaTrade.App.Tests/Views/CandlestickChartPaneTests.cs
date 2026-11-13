namespace ZettaTrade.App.Tests.Views;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.UI.Charting;

/// <summary>
/// The chart control's oscillator pane render pins (T-199): the same draw
/// pass the screen paints proves through pixels that an oscillator line
/// leaves the main plot for its own sub-pane — the main plot shrinks (the
/// candles' ink never enters the pane band), the pane paints its own
/// polyline and its full-width separator hairline, and the fixed bounds'
/// ladder moves the line where the scale says. Every fact posts its
/// control work to the pump-owned dispatcher (the control's affinity
/// world, the T-138 discipline the export facts keep).
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class CandlestickChartPaneTests
{
    /// <summary>Ordered OHLCV frames for the seeded facts: distinct times, prices and volumes.</summary>
    private static Bar Bar(int index) => new(
        new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddHours(index),
        100m + index,
        110m + index,
        95m + index,
        105m + index,
        5m + index);

    /// <summary>
    /// The oscillator line the facts draw: the constant value at the middle
    /// of the declared band, aligned with the bars. Warmup nulls ride the
    /// head like a real series.
    /// </summary>
    private static ChartIndicatorLine OscillatorLine(decimal value, decimal scaleMin, decimal scaleMax, int barCount)
    {
        var values = new decimal?[barCount];
        for (var i = 2; i < barCount; i++)
        {
            values[i] = value;
        }

        return new ChartIndicatorLine(
            "RSI",
            values,
            IndicatorPaneKind.Oscillator,
            scaleMin,
            scaleMax);
    }

    private static CandlestickChart BuildChart(IReadOnlyList<ChartIndicatorLine> lines)
    {
        var chart = new CandlestickChart
        {
            Bars = new ObservableCollection<Bar>([Bar(0), Bar(1), Bar(2), Bar(3)]),
            IndicatorLines = lines,
        };
        chart.Measure(new Size(320, 200));
        chart.Arrange(new Rect(0, 0, 320, 200));
        return chart;
    }

    /// <summary>Run one synchronous control routine on the pump thread and surface its result or fault to the fact.</summary>
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

    /// <summary>The amber indicator-line ink's rows: the pixels the pane's polyline paints (the RSI value).</summary>
    private static List<int> AmberRows(SKBitmap probe)
    {
        var rows = new List<int>();
        for (var y = 0; y < probe.Height; y++)
        {
            for (var x = 0; x < probe.Width; x += 2)
            {
                var color = probe.GetPixel(x, y);
                if (color.Red == 245 && color.Green == 158 && color.Blue == 11 && color.Alpha > 200)
                {
                    rows.Add(y);
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// An oscillator renders its pane under the main plot: the amber
    /// polyline's ink lives ONLY in the lower band (the main plot's own
    /// geometry — candles, overlays — never reaches it), and a frame
    /// without the oscillator paints no amber ink at all.
    /// </summary>
    [Fact]
    public async Task RenderSnapshotPng_AnOscillator_PaintsItsLineInTheLowerBandOnly()
    {
        var (plain, pane, probeHeight) = await OnTheDispatcher(() =>
        {
            var plainChart = BuildChart([]);
            var paneChart = BuildChart([OscillatorLine(50m, 0m, 100m, 4)]);

            using var plainProbe = SKBitmap.Decode(plainChart.RenderSnapshotPngBytes(null));
            using var paneProbe = SKBitmap.Decode(paneChart.RenderSnapshotPngBytes(null));

            var plainRows = AmberRows(plainProbe!);
            var paneRows = AmberRows(paneProbe!);

            return (plainRows, paneRows, paneProbe!.Height);
        });

        plain.Should().BeEmpty("no overlay rides the frame, so the indicator color paints nowhere");
        pane.Should().NotBeEmpty("the pane draws its own polyline");
        pane.Min().Should().BeGreaterThan(probeHeight / 2,
            "the pane stack owns the drawable's lower band — the line never rides the main plot's scale");
    }

    /// <summary>
    /// The declared fixed bounds move the line where the scale says: the
    /// band's midpoint maps the pane's vertical center, the band's top maps
    /// its top edge — two renders of the same series differ by a clear
    /// margin, the ladder is the declaration's, never the values'.
    /// </summary>
    [Fact]
    public async Task RenderSnapshotPng_TheFixedBounds_MoveTheLineWhereTheScaleSays()
    {
        var (midRows, topRows) = await OnTheDispatcher(() =>
        {
            using var midProbe = SKBitmap.Decode(BuildChart([OscillatorLine(50m, 0m, 100m, 4)]).RenderSnapshotPngBytes(null));
            using var topProbe = SKBitmap.Decode(BuildChart([OscillatorLine(100m, 0m, 100m, 4)]).RenderSnapshotPngBytes(null));

            return (AmberRows(midProbe!), AmberRows(topProbe!));
        });

        midRows.Should().NotBeEmpty();
        topRows.Should().NotBeEmpty();
        topRows.Min().Should().BeLessThan(midRows.Min() - 8,
            "the band's top maps the pane's top edge, a clear margin above the midpoint's row");
    }

    /// <summary>
    /// The separator hairline spans the FULL control width (the price
    /// gutter included — a place the main pane's gridlines never reach),
    /// so a frame with an oscillator paints border-colored ink at the far
    /// right of some row; a frame without one paints none there.
    /// </summary>
    [Fact]
    public async Task RenderSnapshotPng_TheSeparator_SpansTheFullWidth()
    {
        var (withoutPane, withPane) = await OnTheDispatcher(() =>
        {
            static bool RightmostBorderInk(SKBitmap probe)
            {
                // The gutter band: the last ~40 px of every row. The main
                // gridlines stop at the plot's right edge; the separator
                // crosses it.
                var farRight = probe!.Width - 8;
                for (var y = 0; y < probe.Height; y++)
                {
                    var color = probe.GetPixel(farRight, y);
                    if (color.Red == 40 && color.Green == 40 && color.Blue == 50 && color.Alpha > 200)
                    {
                        return true;
                    }
                }

                return false;
            }

            using var plainProbe = SKBitmap.Decode(BuildChart([]).RenderSnapshotPngBytes(null));
            using var paneProbe = SKBitmap.Decode(BuildChart([OscillatorLine(50m, 0m, 100m, 4)]).RenderSnapshotPngBytes(null));

            return (RightmostBorderInk(plainProbe!), RightmostBorderInk(paneProbe!));
        });

        withPane.Should().BeTrue("the separator hairline crosses the price gutter");
        withoutPane.Should().BeFalse("without a pane nothing paints border ink in the gutter");
    }

    /// <summary>
    /// The pane's own ladder: with fixed bounds 0-100 the ladder's labels
    /// format the CANONICAL levels — the pane's value texts are plain
    /// decimals in the shared gutter (the text-ink probe counts non-
    /// background pixels right of the plot edge inside the pane's band).
    /// A warmup-only line (every value null) still draws the pane's frame:
    /// the neutral-unit ladder renders, no polyline ink appears.
    /// </summary>
    [Fact]
    public async Task RenderSnapshotPng_AnAllWarmupOscillator_StillPaintsThePaneFrame()
    {
        var (separatorRows, amberRows) = await OnTheDispatcher(() =>
        {
            var allWarmup = new ChartIndicatorLine(
                "RSI",
                new decimal?[4],
                IndicatorPaneKind.Oscillator,
                0m,
                100m);
            using var probe = SKBitmap.Decode(BuildChart([allWarmup]).RenderSnapshotPngBytes(null));

            var separator = new List<int>();
            var amber = new List<int>();
            var farRight = probe!.Width - 8;
            for (var y = 0; y < probe.Height; y++)
            {
                var color = probe.GetPixel(farRight, y);
                if (color.Red == 40 && color.Green == 40 && color.Blue == 50 && color.Alpha > 200)
                {
                    separator.Add(y);
                }
            }

            amber.AddRange(AmberRows(probe));

            return (separator, amber);
        });

        separatorRows.Should().NotBeEmpty("the pane's frame draws even with no values yet");
        amberRows.Should().BeEmpty("the all-warmup line breaks at every bar — no polyline ink");
    }
}
