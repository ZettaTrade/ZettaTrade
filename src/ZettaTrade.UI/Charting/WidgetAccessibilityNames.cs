namespace ZettaTrade.UI.Charting;

using System;
using System.Globalization;
using Livery.Localization;
using ZettaTrade.UI.Resources;

/// <summary>
/// The canvas widgets' accessibility names (T-110): the pure compositors the
/// nested peers ride, plus the one shared template resolver. The compositors
/// are pure datum-in/string-out functions so the pins test them without a
/// dispatcher; the resolver is the widgets' established localization handoff
/// (the live manager when the app wired it, the raw resx catalog otherwise)
/// and carries every <c>*.A11y*</c> key this family reads.
/// </summary>
public static class WidgetAccessibilityNames
{
    #region Resx Keys

    /// <summary>The chart's name when nothing is loaded.</summary>
    private const string ChartNoSeriesKey = "Chart.A11yNoSeries";

    /// <summary>The chart's name template for one main series ("{0}" = symbol).</summary>
    private const string ChartSeriesKey = "Chart.A11ySeries";

    /// <summary>
    /// The chart's name template compared against a second series
    /// ("{0}" = main symbol, "{1}" = the compared labels).
    /// </summary>
    private const string ChartWithCompareKey = "Chart.A11yWithCompare";

    /// <summary>The order book's static title key.</summary>
    private const string OrderBookEmptyKey = "OrderBook.Title";

    /// <summary>The order book's symbol template key ("{0}" = symbol).</summary>
    private const string OrderBookWithSymbolKey = "OrderBook.A11yWithSymbol";

    /// <summary>The depth chart's static title key.</summary>
    private const string DepthEmptyKey = "OrderBook.DepthTitle";

    /// <summary>The depth chart's symbol template key ("{0}" = symbol).</summary>
    private const string DepthWithSymbolKey = "OrderBook.A11yDepthWithSymbol";

    /// <summary>The trade tape's static title key.</summary>
    private const string TapeEmptyKey = "Tape.Title";

    /// <summary>The trade tape's symbol template key ("{0}" = symbol).</summary>
    private const string TapeWithSymbolKey = "Tape.A11yWithSymbol";

    /// <summary>The market heatmap's static title key.</summary>
    private const string HeatmapEmptyKey = "Heatmap.Title";

    #endregion

    #region Pure Compositors

    /// <summary>
    /// The name of a symbol-bearing widget (order book, depth chart, trade
    /// tape): the localized title when the widget holds nothing, the
    /// symbol-composed name otherwise. A data-bearing widget with an empty
    /// symbol text stays on the plain title — a name is never assembled
    /// around a hole.
    /// </summary>
    public static string SymbolRoleName(
        string emptyTemplate,
        string withSymbolTemplate,
        bool hasData,
        string? symbolText)
    {
        return hasData && !string.IsNullOrWhiteSpace(symbolText)
            ? string.Format(CultureInfo.CurrentCulture, withSymbolTemplate, symbolText)
            : emptyTemplate;
    }

    /// <summary>
    /// The chart's name: honestly titled when no series is loaded, the
    /// symbol-named chart for one main series, and the compared-with name on
    /// top of that when compare layers ride the workspace.
    /// </summary>
    public static string ChartName(
        string noSeriesTemplate,
        string seriesTemplate,
        string withCompareTemplate,
        bool hasBars,
        string? mainSeriesLabel,
        string? compareLabelsText)
    {
        if (!hasBars || string.IsNullOrWhiteSpace(mainSeriesLabel))
        {
            return noSeriesTemplate;
        }

        if (string.IsNullOrWhiteSpace(compareLabelsText))
        {
            return string.Format(CultureInfo.CurrentCulture, seriesTemplate, mainSeriesLabel);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            withCompareTemplate,
            mainSeriesLabel,
            compareLabelsText);
    }

    #endregion

    #region Template Resolution

    /// <summary>
    /// The localized read over one catalog key: the app's live localization
    /// manager fixes language and switch timing once the app wired it; before
    /// that (tests, design surface) the raw resx catalog answers under the
    /// process culture — the same handoff the drawn widgets use for their
    /// canvas text.
    /// </summary>
    public static string Template(string key)
    {
        if (LocalizationManager.IsInitialized)
        {
            return LocalizationManager.Instance.GetString(key);
        }

        return Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    /// <summary>The chart peer's template trio for one state walk.</summary>
    public static (string NoSeries, string Series, string WithCompare) ChartTemplates() =>
        (Template(ChartNoSeriesKey), Template(ChartSeriesKey), Template(ChartWithCompareKey));

    /// <summary>The order book peer's template pair.</summary>
    public static (string Empty, string WithSymbol) OrderBookTemplates() =>
        (Template(OrderBookEmptyKey), Template(OrderBookWithSymbolKey));

    /// <summary>The depth chart peer's template pair.</summary>
    public static (string Empty, string WithSymbol) DepthTemplates() =>
        (Template(DepthEmptyKey), Template(DepthWithSymbolKey));

    /// <summary>The trade tape peer's template pair.</summary>
    public static (string Empty, string WithSymbol) TapeTemplates() =>
        (Template(TapeEmptyKey), Template(TapeWithSymbolKey));

    /// <summary>The market heatmap's static title.</summary>
    public static string HeatmapTitle() => Template(HeatmapEmptyKey);

    #endregion
}
