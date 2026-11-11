namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The first-run wizard's view model (T-099): a three-step draft over the
/// theme, the exchange adapter and the workspace symbol. Every selection is
/// a draft with one writer — <see cref="FinishAsync"/> — which validates ALL
/// three picks BEFORE any write lands, so an invalid pick is an error with
/// the pre-wizard state untouched (never a substituting default and never a
/// half-applied set). Skipping is a window concern (a plain close): it runs
/// no write at all — the skipped wizard leaves the pre-wizard state plus the
/// app's shipped defaults, and the once-flag is the launcher launch path's
/// own seam (<see cref="FirstRunWizardState"/>), never the view model's.
/// </summary>
/// <remarks>
/// The theme pick applies through the one seam the settings window uses
/// (the theme manager's apply + the settings persist), the adapter pick
/// rides the facade's <see cref="ActiveExchangeService.SwitchToAsync"/>
/// (it validates the id against the registered catalog, persists the choice
/// and flips the behavioral stream atomically — unknown ids throw, no
/// defaults), and the symbol pick persists the workspace symbol preference
/// the chart's symbol bootstrap restores (the same write the palette and
/// the picker performed before the wizard existed). Secrets never touch the
/// wizard: API keys reach the adapters exclusively through the existing
/// secret-store path.
/// </remarks>
public sealed class FirstRunWizardViewModel : ReactiveObject
{
    #region Constants

    /// <summary>The step index shown first.</summary>
    private const int FirstStepIndex = (int)FirstRunWizardStep.Theme;

    /// <summary>The step index the finish affordance lives on.</summary>
    private const int LastStepIndex = (int)FirstRunWizardStep.Symbol;

    /// <summary>The settings section the theme pick persists through.</summary>
    private const string ThemeSettingsSection = UiSettingsKeys.Section;

    /// <summary>The settings key the persisted theme pick lives under.</summary>
    private const string ThemeSettingKey = UiSettingsKeys.Theme;

    /// <summary>The localized step counter's recipe ("Step {0} of {1}").</summary>
    private const string StepCounterKey = "Wizard.Step.Counter";

    /// <summary>The settings section the workspace symbol preference lives in.</summary>
    private const string WorkspaceSection = UiSettingsKeys.WorkspaceSection;

    /// <summary>The settings key of the persisted workspace symbol pick.</summary>
    private const string WorkspaceSymbolKey = UiSettingsKeys.WorkspaceSymbolId;

    #endregion Constants

    #region Properties

    private int _selectedStepIndex = FirstStepIndex;

    /// <summary>All built-in and custom themes registered for the session; a construction-time snapshot (the set is fixed for a run).</summary>
    public IReadOnlyList<ThemeInfo> ThemeOptions { get; }

    /// <summary>Every exchange adapter registered with the facade; a construction-time snapshot (the set is fixed for a run).</summary>
    public IReadOnlyList<IMarketDataAdapter> AdapterOptions { get; }

    /// <summary>
    /// The draft pick of step one: the theme to apply on finish. Seeded from
    /// the currently applied theme; a failed resolution leaves it unset and
    /// the finish refuses to run (an error, never a substituted default).
    /// </summary>
    public ThemeInfo? SelectedTheme
    {
        get => _selectedTheme;
        set => this.RaiseAndSetIfChanged(ref _selectedTheme, value);
    }

    /// <summary>
    /// The draft pick of step two: the adapter to activate on finish (demo
    /// is the shipped default lane). Seeded from the currently active one.
    /// </summary>
    public IMarketDataAdapter? SelectedAdapter
    {
        get => _selectedAdapter;
        set => this.RaiseAndSetIfChanged(ref _selectedAdapter, value);
    }

    /// <summary>The symbols of the draft adapter, as the listing reports them (normalized names, exchange order).</summary>
    public ObservableCollection<string> SymbolNames { get; } = new();

    /// <summary>
    /// The draft pick of step three: the normalized symbol to persist as the
    /// workspace preference on finish. A draft-adapter switch refills the
    /// listing and an out-of-list pick dies with it.
    /// </summary>
    public string? SelectedSymbolName
    {
        get => _selectedSymbolName;
        set => this.RaiseAndSetIfChanged(ref _selectedSymbolName, value);
    }

    /// <summary>The zero-based index of the visible step.</summary>
    public int SelectedStepIndex
    {
        get => _selectedStepIndex;
        set
        {
            var previous = _selectedStepIndex;
            _selectedStepIndex = value;
            if (previous != _selectedStepIndex)
            {
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(IsThemeStepVisible));
                this.RaisePropertyChanged(nameof(IsAdapterStepVisible));
                this.RaisePropertyChanged(nameof(IsSymbolStepVisible));
                this.RaisePropertyChanged(nameof(IsBackVisible));
                this.RaisePropertyChanged(nameof(IsNextVisible));
                this.RaisePropertyChanged(nameof(IsFinishVisible));
                this.RaisePropertyChanged(nameof(StepCounter));
            }
        }
    }

    /// <summary>Whether the theme step is the visible one.</summary>
    public bool IsThemeStepVisible => _selectedStepIndex == (int)FirstRunWizardStep.Theme;

    /// <summary>Whether the adapter step is the visible one.</summary>
    public bool IsAdapterStepVisible => _selectedStepIndex == (int)FirstRunWizardStep.Adapter;

    /// <summary>Whether the symbol step is the visible one.</summary>
    public bool IsSymbolStepVisible => _selectedStepIndex == LastStepIndex;

    /// <summary>Whether the back affordance has a step behind the visible one.</summary>
    public bool IsBackVisible => _selectedStepIndex > FirstStepIndex;

    /// <summary>Whether there is a next step ahead of the visible one.</summary>
    public bool IsNextVisible => _selectedStepIndex < LastStepIndex;

    /// <summary>Whether the finish affordance is the step's own navigation (the last step's confirm).</summary>
    public bool IsFinishVisible => _selectedStepIndex == LastStepIndex;

    /// <summary>
    /// The localized step counter (T-161): "Step {0} of {1}" over the
    /// 1-based walk — the count reads without the captions, above every
    /// step body. Recomputed per read (the localizer's live recipe); the
    /// step move raises it like the visibility chain.
    /// </summary>
    public string StepCounter => BuildStepCounter();

    /// <summary>Build the counter string the current step index speaks.</summary>
    private string BuildStepCounter() =>
        _localization.GetString(StepCounterKey, _selectedStepIndex + 1, LastStepIndex + 1);

    #endregion Properties

    #region Commands

    /// <summary>Move to the next step (each step stands alone; nothing applies mid-walk).</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextCommand { get; }

    /// <summary>Return to the previous step; the drafts survive the walk.</summary>
    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; }

    /// <summary>Apply and persist the three picks — the view model's single write path.</summary>
    /// <remarks>
    /// The execution emits whether the picks landed: the window closes on a
    /// successful one (View behavior) and stays open on a failed one, where
    /// the drafts survive and a retry or the skip affordance stays honest.
    /// </remarks>
    public ReactiveCommand<RxVoid, bool> FinishCommand { get; }

    #endregion Commands

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IThemeManager _themes;
    private readonly ActiveExchangeService _exchanges;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<FirstRunWizardViewModel> _logger;

    private IMarketDataAdapter? _selectedAdapter;
    private ThemeInfo? _selectedTheme;
    private string? _selectedSymbolName;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the wizard and seed the drafts from the currently applied
    /// state. The seed resolves the applied theme by its canonical name and
    /// the active adapter by reference: both are state the user has already
    /// seen, never a fabricated default. The symbol listing stays empty
    /// until the window attaches the refresh chain
    /// (see <see cref="AttachSymbolListing"/>).
    /// </summary>
    /// <param name="settings">The settings store the picks persist through.</param>
    /// <param name="themeManager">The theme manager the pick applies through (already initialized — the startup walk ran).</param>
    /// <param name="exchanges">The active-exchange facade the adapter pick applies through.</param>
    /// <param name="localization">The localizer the step counter speaks through (T-161).</param>
    /// <param name="logger">The wizard's logger (file-only — Serilog never writes to stdout).</param>
    public FirstRunWizardViewModel(
        ISettingsService settings,
        IThemeManager themeManager,
        ActiveExchangeService exchanges,
        ILocalizationManager localization,
        ILogger<FirstRunWizardViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _themes = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ThemeOptions = themeManager.AvailableThemes;
        AdapterOptions = exchanges.Registered;
        SelectedTheme = ThemeOptions.FirstOrDefault(theme =>
            theme.Name.Equals(themeManager.CurrentTheme, StringComparison.OrdinalIgnoreCase));
        SelectedAdapter = exchanges.Current;

        NextCommand = ReactiveCommand.Create(
            () =>
            {
                SelectedStepIndex = Math.Min(SelectedStepIndex + 1, LastStepIndex);
            },
            this.WhenAnyValue(vm => vm.SelectedStepIndex)
                .Select(index => index < LastStepIndex));
        BackCommand = ReactiveCommand.Create(
            () =>
            {
                SelectedStepIndex = Math.Max(SelectedStepIndex - 1, FirstStepIndex);
            },
            this.WhenAnyValue(vm => vm.SelectedStepIndex)
                .Select(index => index > FirstStepIndex));
        FinishCommand = ReactiveCommand.CreateFromTask<RxVoid, bool>(
            async (_, ct) =>
            {
                try
                {
                    await FinishAsync(ct);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // A cancelled finish is the command's own progress: the
                    // exception rides ReactiveUI's cancellation handling and
                    // applies nothing.
                    throw;
                }
                catch (Exception ex)
                {
                    // A failed finish keeps the wizard open: the
                    // validate-first order applied nothing, so the drafts
                    // are intact and a retry or the skip stays honest.
                    _logger.LogError(ex, "The first-run wizard failed to apply the picks");
                    return false;
                }
            },
            this.WhenAnyValue(vm => vm.SelectedStepIndex)
                .Select(index => index == LastStepIndex));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Attach the symbol listing refresh the window owns: every draft-adapter
    /// selection (including the seeded one) fetches its tradable listing and
    /// the in-flight fetch of a superseded pick is cut by the switch. The
    /// caller disposes the returned subscription with the window
    /// (<c>WhenActivated</c> + <c>DisposeWith</c> — the canon the transient
    /// view model and its only window share).
    /// </summary>
    public IDisposable AttachSymbolListing() => this
        .WhenAnyValue(vm => vm.SelectedAdapter)
        .Select(adapter => adapter is null
            ? Observable.Empty<IReadOnlyList<Symbol>>()
            : Observable.FromAsync((CancellationToken ct) => adapter.GetSymbolsAsync(ct)))
        .Switch()
        .ObserveOnUiThread()
        .Select(static symbols => symbols.Select(static symbol => symbol.Normalized).ToList())
        .Subscribe(
            ReplaceSymbolNames,
            ex => _logger.LogError(ex, "The first-run wizard symbol listing refresh failed"));

    /// <summary>
    /// Apply and persist the three picks, in the settings-window applier's
    /// order (the T-017 ordering follows for the adapter and the symbol):
    /// the theme applies through the manager and persists, the adapter
    /// activates through the facade (its own seam persists the choice and
    /// flips the streams), and the symbol pick persists the workspace
    /// preference the chart's bootstrap applies. All picks validate before
    /// the first write, so an invalid pick is a thrown error with the
    /// pre-wizard state untouched.
    /// </summary>
    /// <param name="ct">The cancellation token from the command's execution.</param>
    public async Task FinishAsync(CancellationToken ct = default)
    {
        // Validate everything first: a null pick, an unregistered theme, an
        // unregistered adapter and an unparsable symbol each end the finish
        // BEFORE the first write — no step ever applies half a set and no
        // invalid pick ever substitutes a default.
        var theme = SelectedTheme
            ?? throw new InvalidOperationException("The theme pick is required: the wizard applies nothing without an explicit selection.");

        if (!ThemeOptions.Any(candidate =>
                string.Equals(candidate.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{theme.Name}' is not a registered theme; the pick is refused, not defaulted.");
        }

        var adapter = SelectedAdapter
            ?? throw new InvalidOperationException("The adapter pick is required: the wizard applies nothing without an explicit selection.");

        if (!AdapterOptions.Any(candidate => ReferenceEquals(candidate, adapter)))
        {
            throw new InvalidOperationException(
                $"'{adapter.Id}' is not a registered exchange adapter; the pick is refused, not defaulted.");
        }

        var symbolName = SelectedSymbolName;
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            throw new InvalidOperationException("The symbol pick is required: the wizard applies nothing without an explicit selection.");
        }

        var symbol = Symbol.Parse(symbolName);

        // All three picks validated — nothing has been applied yet. Each pick
        // lands exactly once, in the settings-window's order.
        await _themes.ApplyThemeAsync(theme.Name, ct);
        await _settings.SetAsync(ThemeSettingsSection, ThemeSettingKey, theme.Name);

        await _exchanges.SwitchToAsync(adapter.Id, ct);

        await _settings.SetAsync(WorkspaceSection, WorkspaceSymbolKey, symbol.Normalized);
        _logger.LogInformation(
            "The first-run wizard applied the picks: theme '{Theme}', adapter '{AdapterId}', symbol '{Symbol}'",
            theme.Name,
            adapter.Id,
            symbol.Normalized);
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Refill the symbol listing in place (the items-control identity never
    /// changes for the binding). A pick that survives stays; the call runs
    /// on the user-interface thread via the attach chain's marshal.
    /// </summary>
    private void ReplaceSymbolNames(IReadOnlyList<string> names)
    {
        SymbolNames.Clear();
        foreach (var name in names)
        {
            SymbolNames.Add(name);
        }

        if (_selectedSymbolName is { } current && !names.Contains(current))
        {
            SelectedSymbolName = null;
        }
    }

    #endregion Private Methods
}
