namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// The Skia header bands breathe (T-179): the tape's and the order book's
/// column captions stop sitting flush on their first data rows. The facts
/// render REAL frames through the headless Skia pipeline (the same custom
/// draw operations the screen paints — the headless drawing backend the
/// world used before T-179 renders neither widget's surface), decode them
/// and scan the ink rows of the header zone: the caption line must keep
/// background-only rows of distance from the first data row's ink, and the
/// band's label line (the book's symbol, the tape's paused hint) must stay
/// a separate line above the captions. The facts ride the
/// <c>LocalizationManagerSingleton</c> collection: the widgets resolve
/// their caption strings through the real localization manager and their
/// colors through the theme singleton, so the manager-creating classes run
/// strictly sequentially with them.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessHeaderBandGapTests
{
    static HeadlessHeaderBandGapTests() => AvaloniaDispatcherPump.EnsureInstalled();

    private const int WindowWidth = 320;
    private const int WindowHeight = 200;

    /// <summary>The scanned frame zone: the band plus the first data rows.</summary>
    private const int ScanHeight = 48;

    /// <summary>A channel difference above this reads as ink, not as antialias dust.</summary>
    private const int InkThreshold = 16;

    /// <summary>
    /// The user ask (the padding between the table header and the data):
    /// at least this many ink-free pixel rows between the caption line's
    /// ink and the first data row's ink.
    /// </summary>
    private const int CaptionToRowGapRows = 2;

    /// <summary>
    /// The band carries two text lines: at least one ink-free row between
    /// the label line's ink and the caption line's ink (the label content
    /// carries no descenders — digits, caps and the slash — so the gap is
    /// measurable; the paused hint's descenders are not: that seam stays
    /// unmeasured, its clearance is a design constant, see the facts' note
    /// below).
    /// </summary>
    private const int LabelToCaptionGapRows = 1;

    /// <summary>Base instant the tape's stamps anchor through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheTapeCaptions_StayClearOfTheFirstTradeRow()
    {
        var subject = new Subject<MarketTrade>();
        var window = await HostAsync(() => new TradeTapeWidget { Trades = subject });
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                subject.OnNext(Trade(0, OrderSide.Buy));
                subject.OnNext(Trade(1, OrderSide.Sell));
                subject.OnNext(Trade(2, OrderSide.Buy));
            });

            var runs = await CaptureInkRunsAsync(window);
            runs.Count.Should().BeGreaterThanOrEqualTo(
                2, "the caption line and the first trade row both ink the scanned zone");
            var gap = runs[1].Top - runs[0].Bottom - 1;
            gap.Should().BeGreaterThanOrEqualTo(
                CaptionToRowGapRows,
                $"the captions must keep {CaptionToRowGapRows}+ background rows clear of the first trade row");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheBookLabelAndCaptions_StayClearOfTheTopLevelRow()
    {
        var window = await HostAsync(() => new OrderBookWidget { Book = SixLevelBook() });
        try
        {
            var runs = await CaptureInkRunsAsync(window);
            runs.Count.Should().BeGreaterThanOrEqualTo(
                3, "the symbol line, the caption line and the top ask row all ink the scanned zone");
            var labelGap = runs[1].Top - runs[0].Bottom - 1;
            labelGap.Should().BeGreaterThanOrEqualTo(
                LabelToCaptionGapRows,
                "the symbol line and the caption line must stay separate lines inside the band");
            var captionGap = runs[2].Top - runs[1].Bottom - 1;
            captionGap.Should().BeGreaterThanOrEqualTo(
                CaptionToRowGapRows,
                $"the captions must keep {CaptionToRowGapRows}+ background rows clear of the top level row");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The paused hint rides the band's label line, ABOVE the captions —
    /// the pre-fix geometry drew it into the caption line's zone. The fact
    /// pins the hint near the band's top and the captions' gap to the first
    /// row; the hint-to-captions seam itself stays unmeasured (the hint's
    /// descenders antialias across a hairline seam, so a pure-row scan
    /// cannot read it — the clearance is the label/caption constants'
    /// business, pinned through the book's descender-free label above).
    /// </summary>
    [Fact]
    public async Task ThePausedHint_RidesTheLabelLine_TheCaptionsKeepTheirGap()
    {
        var subject = new Subject<MarketTrade>();
        var window = await HostAsync(() => new TradeTapeWidget { Trades = subject });
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                subject.OnNext(Trade(0, OrderSide.Buy));
                subject.OnNext(Trade(1, OrderSide.Sell));
                subject.OnNext(Trade(2, OrderSide.Buy));
            });

            // The pause rides the pointer: a move into the widget's middle
            // raises PointerEntered through the real input pipeline.
            await HeadlessWorld.OnUi(() =>
                window.MouseMove(new Point(WindowWidth / 2, WindowHeight / 2)));

            var runs = await CaptureInkRunsAsync(window);
            runs.Count.Should().BeGreaterThanOrEqualTo(
                3, "the hint line, the caption line and the first trade row all ink the scanned zone");
            runs[0].Top.Should().BeLessThanOrEqualTo(
                4, "the hint rides the band's label line near the top edge");
            var captionGap = runs[2].Top - runs[1].Bottom - 1;
            captionGap.Should().BeGreaterThanOrEqualTo(
                CaptionToRowGapRows,
                $"the captions must keep {CaptionToRowGapRows}+ background rows clear of the first trade row even paused");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    #region Harness

    /// <summary>
    /// Host one widget as the whole window content: the widget fills the
    /// client area, so the frame's ink map is exactly the widget's. The
    /// window shows before the fact proceeds — Show is what starts the
    /// window's renderer (MediaContext.AddTopLevel).
    /// </summary>
    private static async Task<Window> HostAsync(Func<Control> contentFactory)
    {
        var window = await HeadlessWorld.OnUi(() => new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = contentFactory(),
        });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>
    /// Capture one rendered frame and fold its scanned zone into ink runs:
    /// consecutive pixel rows carrying any ink pixel (any channel farther
    /// from the background sample than the threshold). The capture runs on
    /// the serialized dispatcher — <c>CaptureRenderedFrame</c> drains the
    /// queue and forces the render ticks itself, so the frame is the
    /// settled post-action one.
    /// </summary>
    private static async Task<IReadOnlyList<InkRun>> CaptureInkRunsAsync(Window window)
    {
        var bitmap = await HeadlessWorld.OnUi(() => window.CaptureRenderedFrame());
        bitmap.Should().NotBeNull("the headless window must have rastered a frame");
        using (bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            using var frame = SKBitmap.Decode(stream);
            frame.Should().NotBeNull("the captured frame must decode");

            return ScanInkRuns(frame);
        }
    }

    private static List<InkRun> ScanInkRuns(SKBitmap frame)
    {
        var width = Math.Min(frame.Width, WindowWidth);
        var height = Math.Min(frame.Height, ScanHeight);

        // The frame's bottom-right corner: below the last planned data row,
        // past every bar and glyph — pure background by construction.
        var background = frame.GetPixel(frame.Width - 1, frame.Height - 1);

        var runs = new List<InkRun>();
        int? runTop = null;
        for (var y = 0; y < height; y++)
        {
            var inked = false;
            for (var x = 0; x < width && !inked; x++)
            {
                var pixel = frame.GetPixel(x, y);
                inked =
                    Math.Abs(pixel.Red - background.Red) > InkThreshold ||
                    Math.Abs(pixel.Green - background.Green) > InkThreshold ||
                    Math.Abs(pixel.Blue - background.Blue) > InkThreshold;
            }

            if (inked && runTop is null)
            {
                runTop = y;
            }
            else if (!inked && runTop is int top)
            {
                runs.Add(new InkRun(top, y - 1));
                runTop = null;
            }
        }

        if (runTop is int lastTop)
        {
            runs.Add(new InkRun(lastTop, height - 1));
        }

        return runs;
    }

    private static MarketTrade Trade(int index, OrderSide side) =>
        new()
        {
            Id = $"t{index}",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = side,
            Price = new Price(100.5m - (0.5m * index)),
            Quantity = new Quantity(2.5m + index),
            Timestamp = Base.AddSeconds(index),
        };

    private static OrderBook SixLevelBook() =>
        new()
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = Enumerable.Range(0, 6).Select(i => new PriceLevel(100m - i, i + 1)).ToList(),
            Asks = Enumerable.Range(0, 6).Select(i => new PriceLevel(100.01m + i, i + 1)).ToList(),
            Timestamp = Base,
        };

    private sealed record InkRun(int Top, int Bottom);

    #endregion
}
