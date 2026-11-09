namespace ZettaTrade.UI.Tests.Charting;

using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// The canvas widgets' accessibility name compositors (T-110): pure
/// datum-in/name-out goldens — the empty state keeps the widget's own
/// localized title, a symbol-bearing state composes the with-symbol
/// template, and the chart walks its three-state ladder (no series, one
/// main series, a compared second series) honestly. The localized
/// templates enter as parameters, so these facts never touch the
/// singleton or the culture.
/// </summary>
public sealed class WidgetAccessibilityNamesTests
{
    private const string EmptyTemplate = "Book: {0}";

    private const string WithSymbolTemplate = "Book for {0}";

    private const string NoSeriesTemplate = "Chart (empty)";

    private const string SeriesTemplate = "Chart: {0}";

    private const string WithCompareTemplate = "Chart: {0} vs {1}";

    #region Symbol-Bearing Family

    [Fact]
    public void NoData_KeepsThePlainTitle()
    {
        WidgetAccessibilityNames.SymbolRoleName(EmptyTemplate, WithSymbolTemplate, false, "BTC/USDT")
            .Should().Be(EmptyTemplate);
    }

    [Fact]
    public void DataWithoutSymbolText_KeepsThePlainTitle()
    {
        WidgetAccessibilityNames.SymbolRoleName(EmptyTemplate, WithSymbolTemplate, true, null)
            .Should().Be(EmptyTemplate);
    }

    [Fact]
    public void WhitespaceSymbolText_NeverComposesAPartialName()
    {
        WidgetAccessibilityNames.SymbolRoleName(EmptyTemplate, WithSymbolTemplate, true, "   ")
            .Should().Be(EmptyTemplate);
    }

    [Fact]
    public void DataWithSymbol_ComposesTheWithSymbolName()
    {
        WidgetAccessibilityNames.SymbolRoleName(EmptyTemplate, WithSymbolTemplate, true, "BTC/USDT")
            .Should().Be("Book for BTC/USDT");
    }

    #endregion

    #region Chart Family

    [Fact]
    public void NoBars_KeepsTheNoSeriesTitle()
    {
        WidgetAccessibilityNames.ChartName(NoSeriesTemplate, SeriesTemplate, WithCompareTemplate, false, "BTC/USDT", null)
            .Should().Be(NoSeriesTemplate);
    }

    [Fact]
    public void BarsWithoutLabel_StayHonestOnTheNoSeriesTitle()
    {
        WidgetAccessibilityNames.ChartName(NoSeriesTemplate, SeriesTemplate, WithCompareTemplate, true, "", null)
            .Should().Be(NoSeriesTemplate);
    }

    [Fact]
    public void OneSeries_ComposesTheChartName()
    {
        WidgetAccessibilityNames.ChartName(NoSeriesTemplate, SeriesTemplate, WithCompareTemplate, true, "BTC/USDT", null)
            .Should().Be("Chart: BTC/USDT");
    }

    [Fact]
    public void CompareLayers_ShareTheFrameInOneName()
    {
        WidgetAccessibilityNames.ChartName(
                NoSeriesTemplate,
                SeriesTemplate,
                WithCompareTemplate,
                true,
                "BTC/USDT",
                string.Join(", ", new[] { "ETH/USDT", "SOL/USDT" }))
            .Should().Be("Chart: BTC/USDT vs ETH/USDT, SOL/USDT");
    }

    #endregion
}
