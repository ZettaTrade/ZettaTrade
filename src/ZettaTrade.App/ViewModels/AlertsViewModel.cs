namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.ValueObjects;

/// <summary>The status byte the commands exchange (the app standard alias).</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The alert manager view model (T-096, T-204): renders the engine's roster as the
/// manager table, composes the editor's drafts (create, edit re-arm, snooze,
/// delete — the fire mechanics riding the re-arm, cooldown and sound pickers),
/// and mirrors the engine's events plus the live language — every
/// row text re-renders on a language switch. The engine stays the roster's
/// only writer; garbage spellings refuse the operation each with its own
/// localized line, never a silently defaulted alert.
/// </summary>
/// <remarks>
/// View lifecycle note: the model owns only dynamic state; the window
/// activation calls <see cref="Attach"/> and disposes the returned handle on
/// close (the attach pattern), so the singleton engine never retains a dead
/// window's subscription.
/// </remarks>
public sealed class AlertsViewModel : ReactiveObject
{
    #region Resx Keys

    private const string InvalidateSymbolKey = "Alert.Error.InvalidSymbol";
    private const string InvalidateTargetKey = "Alert.Error.InvalidTarget";
    private const string InvalidatePercentKey = "Alert.Error.InvalidPercent";
    private const string InvalidateWindowKey = "Alert.Error.InvalidWindow";
    private const string InvalidatePeriodKey = "Alert.Error.InvalidPeriod";
    private const string InvalidateLevelKey = "Alert.Error.InvalidLevel";
    private const string InvalidateFundingThresholdKey = "Alert.Error.InvalidFundingThreshold";
    private const string NoIndicatorsKey = "Alert.Error.NoIndicatorsLoaded";
    private const string InvalidateOperationKey = "Alert.Error.OperationFailed";
    private const string InvalidateCooldownKey = "Alert.Error.InvalidCooldown";
    private const string StatusArmedKey = "Alert.Status.Armed";
    private const string StatusSnoozedFormatKey = "Alert.Status.Snoozed.Format";
    private const string StatusFiredKey = "Alert.Status.Fired";
    private const string StatusPausedKey = "Alert.Status.Paused";
    private const string LastFiredNeverKey = "Alert.Status.Never";
    private const string StatusLastFiredFormatKey = "Alert.Status.LastFired.Format";

    #endregion Resx Keys

    #region Constants

    /// <summary>The percent-move window the editor seeds for a fresh alert (the roster preset).</summary>
    private const string DefaultWindowText = "15";

    /// <summary>The percent threshold the editor seeds for a fresh alert (the roster preset).</summary>
    private const string DefaultPercentText = "5";

    /// <summary>The indicator period the editor seeds for a fresh cross alert (the oscillators' canonical 14).</summary>
    private const string DefaultPeriodText = "14";

    /// <summary>The cross level the editor seeds for a fresh cross alert (the oscillators' canonical upper band).</summary>
    private const string DefaultLevelText = "70";

    /// <summary>The cooldown minutes the editor seeds for a fresh cooldown re-arm (the roster's snooze preset).</summary>
    private const string DefaultCooldownText = "15";

    #endregion Constants

    #region Private Fields

    private readonly IAlertEngine _engine;
    private readonly IIndicatorCatalog _indicators;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<AlertsViewModel> _logger;

    private string _symbolText = string.Empty;
    private AlertKindOption _selectedKind;
    private AlertTriggerOption _selectedTrigger;
    private string _targetText = string.Empty;
    private string _percentText = DefaultPercentText;
    private string _windowText = DefaultWindowText;
    private string _selectedIndicatorName = string.Empty;
    private AlertSourceOption _selectedSource = null!;
    private string _periodText = DefaultPeriodText;
    private string _levelText = DefaultLevelText;
    private AlertReArmOption _selectedReArm = null!;
    private string _cooldownText = DefaultCooldownText;
    private AlertSoundOption _selectedSound = null!;
    private string _noteText = string.Empty;
    private AlertsRowViewModel? _selectedRow;
    private string? _errorMessage;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create a manager view model over the roster's engine.</summary>
    /// <param name="engine">The alert engine (the roster's sole owner and evaluator).</param>
    /// <param name="indicators">The loaded plugin indicator catalog (T-203): the cross editor's picker reads the loaded names.</param>
    /// <param name="localization">The localized strings resolver (live language switching).</param>
    /// <param name="logger">The view model's logger.</param>
    public AlertsViewModel(
        IAlertEngine engine,
        IIndicatorCatalog indicators,
        ILocalizationManager localization,
        ILogger<AlertsViewModel> logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ComposeOptionLabels();
        _selectedKind = KindOptions[0];
        _selectedTrigger = TriggerOptions[1];
        _selectedSource = SourceOptions[0];
        _selectedReArm = ReArmOptions[0];
        _selectedSound = SoundOptions[1];

        CreateCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleCreateAsync(cancellationToken));
        UpdateSelectedCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleUpdateSelectedAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.SelectedRow).Select(static row => row is not null));
        SnoozeSelectedCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleSnoozeSelectedAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.SelectedRow).Select(static row => row is not null));
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleDeleteSelectedAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.SelectedRow).Select(static row => row is not null));

        RebuildRows();
    }

    #endregion Constructors

    #region Resx Option Keys

    private const string KindPriceKey = "Alert.Kind.Price";
    private const string KindPercentKey = "Alert.Kind.PercentMove";
    private const string KindIndicatorKey = "Alert.Kind.IndicatorCross";
    private const string KindFundingKey = "Alert.Kind.FundingCross";
    private const string SourceIndicatorValueKey = "Alert.Source.IndicatorValue";
    private const string SourcePriceKey = "Alert.Source.Price";
    private const string TriggerAboveKey = "Alert.Trigger.Above";
    private const string TriggerBelowKey = "Alert.Trigger.Below";
    private const string TriggerAtKey = "Alert.Trigger.At";
    private const string ReArmOneShotKey = "Alert.ReArm.OneShot";
    private const string ReArmPerCrossKey = "Alert.ReArm.PerCross";
    private const string ReArmCooldownKey = "Alert.ReArm.Cooldown";
    private const string SoundSilentKey = "Alert.Sound.Silent";
    private const string SoundBeepKey = "Alert.Sound.Beep";

    #endregion Resx Option Keys

    #region Table

    /// <summary>The manager's rows (the engine's roster, rendered).</summary>
    public ObservableCollection<AlertsRowViewModel> Rows { get; } = [];

    /// <summary>True while at least one alert exists (the honest empty state otherwise).</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>The selected row (the editor loads and the row commands key on it).</summary>
    public AlertsRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            if (value is { } row)
            {
                LoadEditor(row);
            }
        }
    }

    #endregion Table

    #region Editor Form

    /// <summary>The alert kinds the editor offers (localized labels, recomposed on language changes).</summary>
    public ObservableCollection<AlertKindOption> KindOptions { get; } = [];

    /// <summary>The trigger edges the editor offers (localized labels, recomposed on language changes).</summary>
    public ObservableCollection<AlertTriggerOption> TriggerOptions { get; } = [];

    /// <summary>
    /// The trigger edges the CURRENT kind's vocabulary offers (T-203, T-240):
    /// the indicator cross and the funding cross read only Above/Below — At
    /// is meaningless for a cross and the editor never offers what it would
    /// refuse.
    /// </summary>
    public IReadOnlyList<AlertTriggerOption> VisibleTriggerOptions =>
        IsCrossKind ? _crossTriggerOptions : TriggerOptions;

    /// <summary>The cross vocabulary's edges (Above + Below; composed beside <see cref="TriggerOptions"/>).</summary>
    private readonly List<AlertTriggerOption> _crossTriggerOptions = [];

    /// <summary>The cross sources the editor offers (localized labels, recomposed on language changes).</summary>
    public ObservableCollection<AlertSourceOption> SourceOptions { get; } = [];

    /// <summary>The re-arm modes the editor offers (T-204; localized labels, recomposed on language changes).</summary>
    public ObservableCollection<AlertReArmOption> ReArmOptions { get; } = [];

    /// <summary>The fire sound choices the editor offers (T-204; localized labels, recomposed on language changes).</summary>
    public ObservableCollection<AlertSoundOption> SoundOptions { get; } = [];

    /// <summary>The loaded indicators' canonical names (the session-fixed plugin snapshot).</summary>
    public ObservableCollection<string> IndicatorOptions { get; } = [];

    /// <summary>The symbol spelling the next draft composes with (normalized on success).</summary>
    public string SymbolText
    {
        get => _symbolText;
        set => this.RaiseAndSetIfChanged(ref _symbolText, value);
    }

    /// <summary>The kind the next draft composes with.</summary>
    public AlertKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null)
            {
                // The TwoWay SelectedItem bindings write null while their
                // picker's items are still empty or coercing (the bind/init
                // race, T-225): a null is never a user choice, so the
                // standing option stays and one deferred raise pushes it
                // back into the picker once its items exist.
                SelectionReAssert.Later(this, nameof(SelectedKind));
                return;
            }

            if (_selectedKind == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedKind, value);

            // The kind's own field columns, the source flags (they AND with
            // the kind) and the trigger vocabulary all ride this selection:
            // every dependent fact re-renders, and a cross selection that
            // held At moves to a real cross edge (the editor never shows an
            // option it would refuse).
            this.RaisePropertyChanged(nameof(IsPriceKind));
            this.RaisePropertyChanged(nameof(IsPercentKind));
            this.RaisePropertyChanged(nameof(IsIndicatorKind));
            this.RaisePropertyChanged(nameof(IsFundingKind));
            this.RaisePropertyChanged(nameof(ShowsTrigger));
            this.RaisePropertyChanged(nameof(IsIndicatorValueSource));
            this.RaisePropertyChanged(nameof(IsPriceSource));
            this.RaisePropertyChanged(nameof(VisibleTriggerOptions));
            if (IsCrossKind && _selectedTrigger.Trigger == AlertTrigger.At)
            {
                SelectedTrigger = TriggerOptions.First(option => option.Trigger == AlertTrigger.Below);
            }
        }
    }

    /// <summary>The trigger edge the next draft composes with (the current kind's own vocabulary).</summary>
    public AlertTriggerOption SelectedTrigger
    {
        get => _selectedTrigger;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): the standing
                // option stays, the deferred raise re-asserts it.
                SelectionReAssert.Later(this, nameof(SelectedTrigger));
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedTrigger, value);
        }
    }

    /// <summary>The target-price spelling (Price kind) and the funding threshold's, in PERCENT units (FundingCross kind, T-240); parsed and validated at submit.</summary>
    public string TargetText
    {
        get => _targetText;
        set => this.RaiseAndSetIfChanged(ref _targetText, value);
    }

    /// <summary>The percent-threshold spelling (PercentMove kind; parsed and validated at submit).</summary>
    public string PercentText
    {
        get => _percentText;
        set => this.RaiseAndSetIfChanged(ref _percentText, value);
    }

    /// <summary>The window spelling, minutes (PercentMove kind; parsed and validated at submit).</summary>
    public string WindowText
    {
        get => _windowText;
        set => this.RaiseAndSetIfChanged(ref _windowText, value);
    }

    /// <summary>The cross source the next draft composes with (T-203: what crosses what).</summary>
    public AlertSourceOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): the standing
                // option stays, the deferred raise re-asserts it.
                SelectionReAssert.Later(this, nameof(SelectedSource));
                return;
            }

            if (_selectedSource == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedSource, value);

            // The level field belongs to the indicator-value source only.
            this.RaisePropertyChanged(nameof(IsIndicatorValueSource));
            this.RaisePropertyChanged(nameof(IsPriceSource));
        }
    }

    /// <summary>The indicator the next draft composes with (a loaded plugin's canonical name).</summary>
    public string SelectedIndicatorName
    {
        get => _selectedIndicatorName;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): a null is never
                // a user choice, the standing name stays.
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedIndicatorName, value);
        }
    }

    /// <summary>The period spelling (IndicatorCross kind; parsed and validated at submit).</summary>
    public string PeriodText
    {
        get => _periodText;
        set => this.RaiseAndSetIfChanged(ref _periodText, value);
    }

    /// <summary>The level spelling (IndicatorCross kind, indicator-value source; parsed and validated at submit).</summary>
    public string LevelText
    {
        get => _levelText;
        set => this.RaiseAndSetIfChanged(ref _levelText, value);
    }

    /// <summary>The re-arm mode the next draft composes with (T-204).</summary>
    public AlertReArmOption SelectedReArm
    {
        get => _selectedReArm;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): the standing
                // option stays, the deferred raise re-asserts it.
                SelectionReAssert.Later(this, nameof(SelectedReArm));
                return;
            }

            if (_selectedReArm == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedReArm, value);

            // The cooldown window belongs to the cooldown mode only.
            this.RaisePropertyChanged(nameof(IsCooldownMode));
        }
    }

    /// <summary>The cooldown spelling, minutes (Cooldown re-arm; parsed and validated at submit).</summary>
    public string CooldownText
    {
        get => _cooldownText;
        set => this.RaiseAndSetIfChanged(ref _cooldownText, value);
    }

    /// <summary>The fire sound the next draft composes with (T-204).</summary>
    public AlertSoundOption SelectedSound
    {
        get => _selectedSound;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): the standing
                // option stays, the deferred raise re-asserts it.
                SelectionReAssert.Later(this, nameof(SelectedSound));
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedSound, value);
        }
    }

    /// <summary>True while the Cooldown re-arm mode is selected (its window field shows). Null-tolerant: the bind/init race may transiently leave the selection unset (T-225).</summary>
    public bool IsCooldownMode => SelectedReArm?.Mode == AlertReArmMode.Cooldown;

    /// <summary>The user's free-text note (carried as-is).</summary>
    public string NoteText
    {
        get => _noteText;
        set => this.RaiseAndSetIfChanged(ref _noteText, value);
    }

    /// <summary>True while the Price kind is selected (its fields visible, the percent fields hidden). Null-tolerant: the bind/init race may transiently leave the selection unset (T-225) — the flags answer false, the layout settles once the picker matches.</summary>
    public bool IsPriceKind => SelectedKind?.Kind == PriceAlertKind.Price;

    /// <summary>True while the PercentMove kind is selected (its fields visible, the price fields hidden).</summary>
    public bool IsPercentKind => SelectedKind?.Kind == PriceAlertKind.PercentMove;

    /// <summary>True while the selected kind reads the trigger edge vocabulary (Price, IndicatorCross and FundingCross; the PercentMove kind leaves the neutral At slot untouchable).</summary>
    public bool ShowsTrigger => IsPriceKind || IsCrossKind;

    /// <summary>True while the IndicatorCross kind is selected (T-203: its indicator fields visible, the others hidden).</summary>
    public bool IsIndicatorKind => SelectedKind?.Kind == PriceAlertKind.IndicatorCross;

    /// <summary>True while the FundingCross kind is selected (T-240: its threshold field shows in the price slot, the indicator fields stay hidden).</summary>
    public bool IsFundingKind => SelectedKind?.Kind == PriceAlertKind.FundingCross;

    /// <summary>True while the selected kind is a cross vocabulary kind (T-203/T-240: Above/Below only, At never offered).</summary>
    public bool IsCrossKind => IsIndicatorKind || IsFundingKind;

    /// <summary>True while the cross reads the indicator's own value against the stored level (the level field shows).</summary>
    public bool IsIndicatorValueSource =>
        IsIndicatorKind && SelectedSource?.Source == AlertCrossSource.IndicatorValue;

    /// <summary>True while the cross reads the close against the indicator's value (the level field hides).</summary>
    public bool IsPriceSource =>
        IsIndicatorKind && SelectedSource?.Source == AlertCrossSource.Price;

    #endregion Editor Form

    #region Commands

    /// <summary>Create one alert from the editor (the draft validates before the engine ever sees it).</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateCommand { get; }

    /// <summary>Re-arm the selected alert with the editor's fresh facts.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UpdateSelectedCommand { get; }

    /// <summary>Push the snooze window forward for the selected alert.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SnoozeSelectedCommand { get; }

    /// <summary>Delete the selected alert (the explicit user forgetting).</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; }

    #endregion Commands

    #region Error Surface

    /// <summary>The localized refusal or failure line; null while clean.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    #endregion Error Surface

    #region Lifecycle

    /// <summary>
    /// Attach the transient window's listeners: the engine's event stream
    /// (rows re-render on roster changes and fires) and the language change
    /// stream (row texts re-resolve live). The caller owns the returned
    /// handle — the attach pattern.
    /// </summary>
    /// <returns>The attach handle; disposing stops both mirrors.</returns>
    public IDisposable Attach()
    {
        var composite = new CompositeDisposable();

        composite.Add(_engine.Events
            .ObserveOnUiThread()
            .Subscribe(
                _ => RebuildRows(),
                ex => _logger.LogError(ex, "The alert event mirror stopped unexpectedly")));

        composite.Add(_localization.LanguageChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => RenderOnLanguageChange(),
                ex => _logger.LogError(ex, "The alert language mirror stopped unexpectedly")));

        return composite;
    }

    #endregion Lifecycle

    #region Command Handlers

    private async Task<RxVoid> HandleCreateAsync(CancellationToken cancellationToken)
    {
        if (!TryComposeDraft(out var draft, out var inputError))
        {
            ErrorMessage = inputError;
            return RxVoid.Default;
        }

        try
        {
            await _engine.CreateAsync(draft, cancellationToken).ConfigureAwait(false);
            ErrorMessage = null;
            SymbolText = string.Empty;
            TargetText = string.Empty;
            NoteText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating the alert failed");
            ErrorMessage = _localization.GetString(InvalidateOperationKey, ex.Message);
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleUpdateSelectedAsync(CancellationToken cancellationToken)
    {
        // No selection never reaches here through the UI (the command's
        // canExecute gates it); the honest reply is the no-op.
        if (SelectedRow is not { } selection)
        {
            return RxVoid.Default;
        }

        if (!TryComposeDraft(out var draft, out var inputError))
        {
            ErrorMessage = inputError;
            return RxVoid.Default;
        }

        try
        {
            await _engine.UpdateAsync(selection.Id, draft, cancellationToken).ConfigureAwait(false);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updating the alert failed");
            ErrorMessage = _localization.GetString(InvalidateOperationKey, ex.Message);
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleSnoozeSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } selection)
        {
            return RxVoid.Default;
        }

        try
        {
            await _engine.SnoozeAsync(selection.Id, cancellationToken).ConfigureAwait(false);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snoozing the alert failed");
            ErrorMessage = _localization.GetString(InvalidateOperationKey, ex.Message);
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleDeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } selection)
        {
            return RxVoid.Default;
        }

        try
        {
            await _engine.DeleteAsync(selection.Id, cancellationToken).ConfigureAwait(false);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the alert failed");
            ErrorMessage = _localization.GetString(InvalidateOperationKey, ex.Message);
        }

        return RxVoid.Default;
    }

    #endregion Command Handlers

    #region Draft Composition

    /// <summary>
    /// Compose the editor's draft: every spelling parses explicitly — an
    /// unparsable field is a refused operation with its own localized line,
    /// never a silently substituted value. The indicator cross (T-203) needs
    /// a loaded indicator, a positive period and — for the indicator-value
    /// source — a numeric level; an empty plugin set refuses the kind
    /// outright (there is nothing to cross).
    /// </summary>
    private bool TryComposeDraft(out AlertDraft draft, out string? inputError)
    {
        draft = null!;
        inputError = null;

        // The pickers' facts must exist to compose anything: the bind/init
        // race's null writes are rejected by the setters, so an unset
        // selection can only arrive through a writer that bypassed them
        // (T-225) — the composition refuses with its own line, never a
        // silently defaulted draft.
        if (SelectedKind is not { } kindOption ||
            SelectedTrigger is not { } triggerOption ||
            SelectedSource is not { } sourceOption ||
            SelectedReArm is not { } reArmOption ||
            SelectedSound is not { } soundOption)
        {
            inputError = _localization.GetString(InvalidateOperationKey, "the editor's pickers are still settling");
            return false;
        }

        Symbol symbol;
        try
        {
            if (!AlertDraft.IsAParsableSpelling(_symbolText))
            {
                inputError = _localization.GetString(InvalidateSymbolKey, _symbolText);
                return false;
            }

            symbol = Symbol.Parse(_symbolText);
        }
        catch (ArgumentException)
        {
            inputError = _localization.GetString(InvalidateSymbolKey, _symbolText);
            return false;
        }

        var target = 0m;
        var percent = 0m;
        var window = 0;
        AlertIndicatorSpec? indicator = null;

        var reArm = reArmOption.Mode;
        var cooldown = 0;
        if (reArm == AlertReArmMode.Cooldown &&
            (!int.TryParse(_cooldownText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out cooldown) ||
             cooldown <= 0))
        {
            inputError = _localization.GetString(InvalidateCooldownKey, _cooldownText);
            return false;
        }

        switch (kindOption.Kind)
        {
            case PriceAlertKind.Price:
                if (!decimal.TryParse(_targetText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out target))
                {
                    inputError = _localization.GetString(InvalidateTargetKey, _targetText);
                    return false;
                }

                break;

            case PriceAlertKind.PercentMove:
                if (!decimal.TryParse(_percentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent))
                {
                    inputError = _localization.GetString(InvalidatePercentKey, _percentText);
                    return false;
                }

                if (!int.TryParse(_windowText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out window))
                {
                    inputError = _localization.GetString(InvalidateWindowKey, _windowText);
                    return false;
                }

                break;

            case PriceAlertKind.IndicatorCross:
                if (_indicators.LoadedNames.Count == 0 || string.IsNullOrWhiteSpace(_selectedIndicatorName))
                {
                    inputError = _localization.GetString(NoIndicatorsKey);
                    return false;
                }

                if (!int.TryParse(_periodText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var period) ||
                    period <= 0)
                {
                    inputError = _localization.GetString(InvalidatePeriodKey, _periodText);
                    return false;
                }

                var level = 0m;
                if (sourceOption.Source == AlertCrossSource.IndicatorValue &&
                    !decimal.TryParse(_levelText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out level))
                {
                    inputError = _localization.GetString(InvalidateLevelKey, _levelText);
                    return false;
                }

                indicator = new AlertIndicatorSpec(_selectedIndicatorName, period, sourceOption.Source, level);
                break;

            case PriceAlertKind.FundingCross:
                // The threshold rides the Price kind's own field slot in
                // PERCENT units (T-240); zero and negatives are honest
                // thresholds, so only the parse is demanded — the draft
                // validation demands nothing further.
                if (!decimal.TryParse(_targetText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out target))
                {
                    inputError = _localization.GetString(InvalidateFundingThresholdKey, _targetText);
                    return false;
                }

                break;

            default:
                inputError = _localization.GetString(InvalidateOperationKey, "unknown alert kind");
                return false;
        }

        draft = new AlertDraft(
            SymbolName: symbol.Normalized,
            Kind: kindOption.Kind,
            Trigger: triggerOption.Trigger,
            TargetPrice: target,
            ThresholdPercent: percent,
            WindowMinutes: window,
            Note: _noteText,
            Indicator: indicator,
            ReArm: reArm,
            CooldownMinutes: cooldown,
            Sound: soundOption.Sound);
        return true;
    }

    #endregion Draft Composition

    #region Rows and Rendering

    /// <summary>Replace the row set with the engine's current roster (small: a wholesale rebuild keeps it honest).</summary>
    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var definition in _engine.Definitions)
        {
            Rows.Add(RenderRow(definition));
        }

        this.RaisePropertyChanged(nameof(HasRows));
        if (SelectedRow is not null && Rows.All(row => row.Id != SelectedRow.Id))
        {
            // The selection's alert is gone: the editor clears rather than
            // pointing at a dead id (the row set is the truth).
            SelectedRow = null;
        }
    }

    /// <summary>Compose the picker options' localized labels (the first fill and every language change).</summary>
    private void ComposeOptionLabels()
    {
        KindOptions.Clear();
        KindOptions.Add(new AlertKindOption(PriceAlertKind.Price, _localization.GetString(KindPriceKey)));
        KindOptions.Add(new AlertKindOption(PriceAlertKind.PercentMove, _localization.GetString(KindPercentKey)));
        KindOptions.Add(new AlertKindOption(PriceAlertKind.IndicatorCross, _localization.GetString(KindIndicatorKey)));
        KindOptions.Add(new AlertKindOption(PriceAlertKind.FundingCross, _localization.GetString(KindFundingKey)));
        TriggerOptions.Clear();
        TriggerOptions.Add(new AlertTriggerOption(AlertTrigger.Above, _localization.GetString(TriggerAboveKey)));
        TriggerOptions.Add(new AlertTriggerOption(AlertTrigger.Below, _localization.GetString(TriggerBelowKey)));
        TriggerOptions.Add(new AlertTriggerOption(AlertTrigger.At, _localization.GetString(TriggerAtKey)));
        _crossTriggerOptions.Clear();
        _crossTriggerOptions.Add(new AlertTriggerOption(AlertTrigger.Above, _localization.GetString(TriggerAboveKey)));
        _crossTriggerOptions.Add(new AlertTriggerOption(AlertTrigger.Below, _localization.GetString(TriggerBelowKey)));
        SourceOptions.Clear();
        SourceOptions.Add(new AlertSourceOption(AlertCrossSource.IndicatorValue, _localization.GetString(SourceIndicatorValueKey)));
        SourceOptions.Add(new AlertSourceOption(AlertCrossSource.Price, _localization.GetString(SourcePriceKey)));
        ReArmOptions.Clear();
        ReArmOptions.Add(new AlertReArmOption(AlertReArmMode.OneShot, _localization.GetString(ReArmOneShotKey)));
        ReArmOptions.Add(new AlertReArmOption(AlertReArmMode.PerCross, _localization.GetString(ReArmPerCrossKey)));
        ReArmOptions.Add(new AlertReArmOption(AlertReArmMode.Cooldown, _localization.GetString(ReArmCooldownKey)));
        SoundOptions.Clear();
        SoundOptions.Add(new AlertSoundOption(AlertSoundChoice.Silent, _localization.GetString(SoundSilentKey)));
        SoundOptions.Add(new AlertSoundOption(AlertSoundChoice.Beep, _localization.GetString(SoundBeepKey)));
        IndicatorOptions.Clear();
        foreach (var name in _indicators.LoadedNames)
        {
            IndicatorOptions.Add(name);
        }

        this.RaisePropertyChanged(nameof(VisibleTriggerOptions));
    }

    /// <summary>The language mirror's leg: the pickers re-label, the rows re-render, the selection survives.</summary>
    private void RenderOnLanguageChange()
    {
        var kind = SelectedKind.Kind;
        var trigger = SelectedTrigger.Trigger;
        var source = SelectedSource.Source;
        var indicatorName = SelectedIndicatorName;
        var reArm = SelectedReArm.Mode;
        var sound = SelectedSound.Sound;
        var selectedId = SelectedRow?.Id;
        ComposeOptionLabels();
        RebuildRows();

        _selectedKind = KindOptions.First(option => option.Kind == kind);
        this.RaisePropertyChanged(nameof(SelectedKind));
        _selectedTrigger = TriggerOptions.First(option => option.Trigger == trigger);
        this.RaisePropertyChanged(nameof(SelectedTrigger));
        _selectedSource = SourceOptions.First(option => option.Source == source);
        this.RaisePropertyChanged(nameof(SelectedSource));
        _selectedReArm = ReArmOptions.First(option => option.Mode == reArm);
        this.RaisePropertyChanged(nameof(SelectedReArm));
        _selectedSound = SoundOptions.First(option => option.Sound == sound);
        this.RaisePropertyChanged(nameof(SelectedSound));
        if (indicatorName.Length > 0 && !IndicatorOptions.Contains(indicatorName))
        {
            // A stored name whose plugin is not loaded this session stays in
            // the picker (the honest stored fact, never a silent swap).
            IndicatorOptions.Add(indicatorName);
        }

        _selectedIndicatorName = indicatorName;
        this.RaisePropertyChanged(nameof(SelectedIndicatorName));

        if (selectedId is { } id)
        {
            SelectedRow = Rows.FirstOrDefault(row => row.Id == id);
        }
    }

    private void RenderLocalizedRows()
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            var definition = _engine.Definitions.FirstOrDefault(item => item.Id == Rows[index].Id);
            if (definition is not null)
            {
                Rows[index] = RenderRow(definition);
            }
        }
    }

    private AlertsRowViewModel RenderRow(AlertDefinition definition)
    {
        var row = new AlertsRowViewModel
        {
            Id = definition.Id,
            SymbolText = definition.SymbolName,
        };
        ApplyConditionText(row, definition);
        ApplyStatusText(row, definition);
        ApplyLastFiredText(row, definition);
        ApplyNoteText(row, definition);
        return row;
    }

    /// <summary>Load the editor's fields from the selected row's definition.</summary>
    private void LoadEditor(AlertsRowViewModel row)
    {
        var definition = _engine.Definitions.FirstOrDefault(item => item.Id == row.Id);
        if (definition is null)
        {
            return;
        }

        SymbolText = definition.SymbolName;
        SelectedKind = KindOptions.First(option => option.Kind == definition.Kind);
        SelectedTrigger = TriggerOptions.First(option => option.Trigger == definition.Trigger);
        TargetText = definition.TargetPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PercentText = definition.ThresholdPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WindowText = definition.WindowMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NoteText = definition.Note;
        SelectedReArm = ReArmOptions.First(option => option.Mode == definition.ReArm);
        CooldownText = definition.CooldownMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SelectedSound = SoundOptions.First(option => option.Sound == definition.Sound);
        if (definition.Kind == PriceAlertKind.IndicatorCross && definition.Indicator is { } spec)
        {
            if (!IndicatorOptions.Contains(spec.Name))
            {
                // The stored name's plugin is not loaded this session: the
                // picker shows the stored fact rather than silently swapping it.
                IndicatorOptions.Add(spec.Name);
            }

            SelectedIndicatorName = spec.Name;
            SelectedSource = SourceOptions.First(option => option.Source == spec.Source);
            PeriodText = spec.Period.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LevelText = spec.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void ApplyConditionText(AlertsRowViewModel row, AlertDefinition definition) =>
        row.ConditionText = AlertConditionText.Compose(definition, _localization);

    private void ApplyStatusText(AlertsRowViewModel row, AlertDefinition definition) => row.StatusText =
        definition.Status switch
        {
            AlertStatus.Armed => _localization.GetString(StatusArmedKey),
            AlertStatus.Snoozed => definition.SnoozedUntilUtc is { } until
                ? _localization.GetString(StatusSnoozedFormatKey, until)
                : _localization.GetString(StatusArmedKey),
            AlertStatus.Fired => _localization.GetString(StatusFiredKey),
            AlertStatus.Paused => _localization.GetString(StatusPausedKey),
            _ => string.Empty,
        };

    private void ApplyLastFiredText(AlertsRowViewModel row, AlertDefinition definition) => row.LastFiredText =
        definition.LastFiredUtc is { } fired
            ? _localization.GetString(StatusLastFiredFormatKey, fired)
            : _localization.GetString(LastFiredNeverKey);

    private void ApplyNoteText(AlertsRowViewModel row, AlertDefinition definition) => row.NoteText = definition.Note;

    #endregion Rows and Rendering
}
