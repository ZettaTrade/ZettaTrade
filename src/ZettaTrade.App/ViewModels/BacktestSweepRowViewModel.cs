namespace ZettaTrade.App.ViewModels;

using Livery.Localization;
using ReactiveUI;
using ZettaTrade.Backtest;

/// <summary>
/// One ranked row of the sweep's result table (T-212): the grid cell's pair
/// rendered in the live language, the row's headline metrics and the rank
/// the table displays. The metrics ride the row's own statistics (T-263:
/// the engine retains metrics rows, the full report opens through the
/// click-through); the renderings re-resolve on every language change
/// through <see cref="Refresh"/> (the view lifecycle canon — never a static
/// localized string bound once); the numbers render in the current culture
/// exactly like the run report's summary line does.
/// </summary>
public sealed class BacktestSweepRowViewModel : ReactiveObject
{
    #region Resx Keys

    private const string FractionParamKey = "Backtest.Sweep.Param.Fraction";
    private const string SlippageParamKey = "Backtest.Sweep.Param.Slippage";
    private const string RankColumnKey = "Backtest.Sweep.Column.Rank";
    private const string NetColumnKey = "Backtest.Sweep.Column.Net";

    #endregion Resx Keys

    #region Private Fields

    private readonly BacktestSweepRow _row;
    private readonly BacktestSweepParameter _firstParameter;
    private readonly BacktestSweepParameter _secondParameter;

    private string _parameterText = string.Empty;
    private string _netText = string.Empty;
    private string _drawdownText = string.Empty;
    private string _winRateText = string.Empty;
    private string _profitFactorText = string.Empty;
    private string _automationName = string.Empty;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create one table row over the ranked engine row and the grid's two knobs.</summary>
    /// <param name="row">The engine's ranked row (the cell's pair plus its own metrics).</param>
    /// <param name="firstParameter">The grid's first axis' knob.</param>
    /// <param name="secondParameter">The grid's second axis' knob.</param>
    /// <param name="localization">The localized strings resolver (the first render).</param>
    public BacktestSweepRowViewModel(
        BacktestSweepRow row,
        BacktestSweepParameter firstParameter,
        BacktestSweepParameter secondParameter,
        ILocalizationManager localization)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localization);

        _row = row;
        _firstParameter = firstParameter;
        _secondParameter = secondParameter;
        Refresh(localization);
    }

    #endregion Constructors

    #region Facts

    /// <summary>The engine's ranked row (the click-through reads its request).</summary>
    public BacktestSweepRow Row => _row;

    /// <summary>The 1-based position in the ranked table (1 = the best net).</summary>
    public int Rank => _row.Rank;

    /// <summary>The row's net outcome in the quote currency (the ranking's primary metric).</summary>
    public decimal Net => _row.Net;

    /// <summary>The cell's pair rendered with the knobs' live labels ("fraction=0.25, slippage=0").</summary>
    public string ParameterText
    {
        get => _parameterText;
        private set => this.RaiseAndSetIfChanged(ref _parameterText, value);
    }

    /// <summary>The row's net outcome rendered in the current culture.</summary>
    public string NetText
    {
        get => _netText;
        private set => this.RaiseAndSetIfChanged(ref _netText, value);
    }

    /// <summary>The row's replay's largest drawdown rendered in the current culture.</summary>
    public string DrawdownText
    {
        get => _drawdownText;
        private set => this.RaiseAndSetIfChanged(ref _drawdownText, value);
    }

    /// <summary>The row's replay's win rate rendered in the current culture (the honest absence is a dash).</summary>
    public string WinRateText
    {
        get => _winRateText;
        private set => this.RaiseAndSetIfChanged(ref _winRateText, value);
    }

    /// <summary>The row's replay's profit factor rendered in the current culture (the honest absence is a dash).</summary>
    public string ProfitFactorText
    {
        get => _profitFactorText;
        private set => this.RaiseAndSetIfChanged(ref _profitFactorText, value);
    }

    /// <summary>The row's screen-reader name, composed of the localized fragments.</summary>
    public string AutomationName
    {
        get => _automationName;
        private set => this.RaiseAndSetIfChanged(ref _automationName, value);
    }

    #endregion Facts

    #region Localized Render

    /// <summary>Re-render every localized surface (the language change mirror).</summary>
    /// <param name="localization">The localized strings resolver.</param>
    public void Refresh(ILocalizationManager localization)
    {
        ArgumentNullException.ThrowIfNull(localization);

        var firstLabel = KnobLabel(localization, _firstParameter);
        var secondLabel = KnobLabel(localization, _secondParameter);
        ParameterText = $"{firstLabel}={Render(_row.FirstValue)}, {secondLabel}={Render(_row.SecondValue)}";
        NetText = Render(_row.Net);
        DrawdownText = Render(_row.Statistics.MaxDrawdown);
        WinRateText = Render(_row.Statistics.WinRate);
        ProfitFactorText = Render(_row.Statistics.ProfitFactor);
        AutomationName =
            $"{localization.GetString(RankColumnKey)} {_row.Rank}: {ParameterText}, "
            + $"{localization.GetString(NetColumnKey)} {NetText}";
    }

    /// <summary>
    /// One knob's live label (the picker's own lines). An undefined knob
    /// value never reaches here (the engine refuses the axis up front);
    /// the exhaustive switch keeps it that way honestly — no silent default.
    /// </summary>
    private static string KnobLabel(ILocalizationManager localization, BacktestSweepParameter parameter) =>
        localization.GetString(parameter switch
        {
            BacktestSweepParameter.LongNotionalFraction => FractionParamKey,
            BacktestSweepParameter.SlippageValue => SlippageParamKey,
            _ => throw new InvalidOperationException(
                $"The sweep parameter '{parameter}' is not a defined knob; the label is refused, never defaulted."),
        });

    /// <summary>Render one raw decimal in the current culture (the summary's convention).</summary>
    private static string Render(decimal value) => value.ToString(null, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Render one optional decimal in the current culture; the honest absence is a dash, never zero.</summary>
    private static string Render(decimal? value) => value is { } present ? Render(present) : "-";

    #endregion Localized Render
}
