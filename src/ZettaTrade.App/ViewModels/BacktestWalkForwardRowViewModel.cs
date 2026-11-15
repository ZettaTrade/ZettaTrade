namespace ZettaTrade.App.ViewModels;

using Livery.Localization;
using ReactiveUI;
using ZettaTrade.Backtest;

/// <summary>
/// One fold row of the walk-forward's result table (T-254): the fold's own
/// carve, the pair its in-sample optimization chose (rendered with the
/// knobs' live labels, exactly a sweep row's pair) and the chosen pair's
/// honest out-of-sample numbers — the study's whole point. The renderings
/// re-resolve on every language change through <see cref="Refresh"/> (the
/// view lifecycle canon — never a static localized string bound once); the
/// numbers render in the current culture exactly like the sweep table's do.
/// </summary>
public sealed class BacktestWalkForwardRowViewModel : ReactiveObject
{
    #region Resx Keys

    private const string FractionParamKey = "Backtest.Sweep.Param.Fraction";
    private const string SlippageParamKey = "Backtest.Sweep.Param.Slippage";
    private const string FoldColumnKey = "Backtest.WalkForward.Column.Fold";
    private const string OutOfSampleNetColumnKey = "Backtest.WalkForward.Column.OutOfSampleNet";

    #endregion Resx Keys

    #region Private Fields

    private readonly BacktestWalkForwardRow _row;
    private readonly BacktestSweepParameter _firstParameter;
    private readonly BacktestSweepParameter _secondParameter;

    private string _parameterText = string.Empty;
    private string _inSampleNetText = string.Empty;
    private string _outOfSampleNetText = string.Empty;
    private string _outOfSampleDrawdownText = string.Empty;
    private string _automationName = string.Empty;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create one table row over the engine's fold row and the study's two knobs.</summary>
    /// <param name="row">The engine's fold row (the carve, the chosen pair and the honest OOS numbers).</param>
    /// <param name="firstParameter">The study's first axis' knob.</param>
    /// <param name="secondParameter">The study's second axis' knob.</param>
    /// <param name="localization">The localized strings resolver (the first render).</param>
    public BacktestWalkForwardRowViewModel(
        BacktestWalkForwardRow row,
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

    /// <summary>The engine's fold row (the fold's own carve and numbers).</summary>
    public BacktestWalkForwardRow Row => _row;

    /// <summary>The 1-based fold (chronological over the window).</summary>
    public int Fold => _row.Fold;

    /// <summary>The chosen pair rendered with the knobs' live labels ("fraction=0.5, slippage=1").</summary>
    public string ParameterText
    {
        get => _parameterText;
        private set => this.RaiseAndSetIfChanged(ref _parameterText, value);
    }

    /// <summary>The chosen pair's best in-sample net rendered in the current culture.</summary>
    public string InSampleNetText
    {
        get => _inSampleNetText;
        private set => this.RaiseAndSetIfChanged(ref _inSampleNetText, value);
    }

    /// <summary>The chosen pair's out-of-sample net rendered in the current culture (the honest headline).</summary>
    public string OutOfSampleNetText
    {
        get => _outOfSampleNetText;
        private set => this.RaiseAndSetIfChanged(ref _outOfSampleNetText, value);
    }

    /// <summary>The chosen pair's out-of-sample replay's largest drawdown rendered in the current culture.</summary>
    public string OutOfSampleDrawdownText
    {
        get => _outOfSampleDrawdownText;
        private set => this.RaiseAndSetIfChanged(ref _outOfSampleDrawdownText, value);
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
        InSampleNetText = Render(_row.InSampleNet);
        OutOfSampleNetText = Render(_row.OutOfSampleNet);
        OutOfSampleDrawdownText = Render(_row.OutOfSampleMaxDrawdown);
        AutomationName =
            $"{localization.GetString(FoldColumnKey)} {_row.Fold}: {ParameterText}, "
            + $"{localization.GetString(OutOfSampleNetColumnKey)} {OutOfSampleNetText}";
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

    #endregion Localized Render
}
