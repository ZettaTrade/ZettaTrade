namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;
using ZettaTrade.UI.Resources;

/// <summary>
/// The Skia canvas widgets' live accessibility peers (T-110): every widget
/// the base Control serves an automation-none peer — a drawn surface the
/// assistive tree never sees. These facts create the real controls on the
/// pump thread and walk the peer through the same route a screen reader
/// takes (create, name, type): the peer is visible, the empty state keeps
/// the widget's localized title, a data state composes the symbol name,
/// and the equity curve's window-attached name rides the peer through the
/// base naming route (the same route every icon-only control is named by).
/// Expected strings resolve in-situ, so the facts pin the localization
/// routing, not a language.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class SkiaWidgetAutomationPeerTests
{
    private static Bar MainBar(int index) => new(
        new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero).AddHours(index),
        100m + index,
        110m + index,
        95m + index,
        105m + index,
        5m + index);

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

    /// <summary>The template's own read in the test process's culture (the same route the compositors resolve through).</summary>
    private static string Template(string key) =>
        Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>The peer's surfaced name and role for one control (the screen reader's read).</summary>
    private static (string Name, AutomationControlType Type) PeerOf(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);

        peer.Should().BeAssignableTo<ControlAutomationPeer>(
            "the widget serves a real peer on the Control naming route, never the automation-none default");

        return (peer.GetName(), peer.GetAutomationControlType());
    }

    #region Candlestick Chart

    [Fact]
    public async Task TheChartNamesItselfForTheLoadedSymbol()
    {
        var (bareName, bareType) = await OnTheDispatcher(() => PeerOf(new CandlestickChart()));

        var (loadedName, _) = await OnTheDispatcher(() => PeerOf(new CandlestickChart
        {
            Bars = new ObservableCollection<Bar>([MainBar(0), MainBar(1)]),
            MainSeriesLabel = "BTC/USDT",
        }));

        bareType.Should().Be(AutomationControlType.Custom);
        bareName.Should().Be(Template("Chart.A11yNoSeries"));
        loadedName.Should().Be(string.Format(Template("Chart.A11ySeries"), "BTC/USDT"));
    }

    [Fact]
    public async Task TheChartDisclosesTheCompareSeriesInItsName()
    {
        var name = await OnTheDispatcher(() => PeerOf(new CandlestickChart
        {
            Bars = new ObservableCollection<Bar>([MainBar(0)]),
            MainSeriesLabel = "BTC/USDT",
            CompareLayers =
            [
                new("ETH/USDT", []),
                new("SOL/USDT", []),
            ],
        }).Name);

        name.Should().Be(string.Format(Template("Chart.A11yWithCompare"), "BTC/USDT", "ETH/USDT, SOL/USDT"));
    }

    #endregion

    #region Book Family

    [Fact]
    public async Task TheBookNamesItsSymbol()
    {
        var (bareName, bareType) = await OnTheDispatcher(() => PeerOf(new OrderBookWidget()));

        var (named, _) = await OnTheDispatcher(() => PeerOf(new OrderBookWidget
        {
            Book = new OrderBook
            {
                Symbol = Symbol.Parse("BTC/USDT"),
                Bids = [new PriceLevel(99m, 1m)],
                Asks = [new PriceLevel(101m, 1m)],
                Timestamp = DateTimeOffset.UtcNow,
            },
        }));

        bareType.Should().Be(AutomationControlType.Custom);
        bareName.Should().Be(Template("OrderBook.Title"));
        named.Should().Be(string.Format(Template("OrderBook.A11yWithSymbol"), "BTC/USDT"));
    }

    [Fact]
    public async Task TheDepthPaneStaysDistinguishableFromTheBook()
    {
        var (bareName, bareType) = await OnTheDispatcher(() => PeerOf(new DepthChartWidget()));

        var (named, _) = await OnTheDispatcher(() => PeerOf(new DepthChartWidget
        {
            Book = new OrderBook
            {
                Symbol = Symbol.Parse("ETH/USDT"),
                Bids = [new PriceLevel(499m, 2m)],
                Asks = [new PriceLevel(501m, 2m)],
                Timestamp = DateTimeOffset.UtcNow,
            },
        }));

        bareType.Should().Be(AutomationControlType.Custom);
        bareName.Should().Be(Template("OrderBook.DepthTitle"));
        named.Should().Be(string.Format(Template("OrderBook.A11yDepthWithSymbol"), "ETH/USDT"));
    }

    #endregion

    #region Tape, Heatmap & Equity

    [Fact]
    public async Task TheTapeKeepsItsTitleBeforeTheFirstTrade()
    {
        var (name, type) = await OnTheDispatcher(() => PeerOf(new TradeTapeWidget()));

        type.Should().Be(AutomationControlType.Custom);
        name.Should().Be(Template("Tape.Title"));
    }

    [Fact]
    public async Task TheHeatmapCarriesItsLocalizedTitle()
    {
        var (name, type) = await OnTheDispatcher(() => PeerOf(new HeatmapWidget()));

        type.Should().Be(AutomationControlType.Custom);
        name.Should().Be(Template("Heatmap.Title"));
    }

    [Fact]
    public async Task TheEquityCurveSurfacesTheWindowAttachedName()
    {
        var (name, type) = await OnTheDispatcher(() =>
        {
            var chart = new EquityCurveChart();
            AutomationProperties.SetName(chart, Template("Backtest.A11yEquity"));

            return PeerOf(chart);
        });

        type.Should().Be(AutomationControlType.Custom);
        name.Should().Be(Template("Backtest.A11yEquity"));
    }

    #endregion
}
