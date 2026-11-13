namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Charting;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The named chart templates' manager (T-202): saves the chart's current
/// face (rendering type, Y-axis scale mode, the indicator configuration and
/// the two overlay toggles) under a name, applies a saved face back, and
/// keeps one template as the DEFAULT — whose per-symbol facets (type, scale
/// mode) seed every symbol that carries no stored choice of its own. The
/// templates live in a versioned envelope under <c>UI.ChartTemplates</c>
/// (see <see cref="ChartTemplatesStore"/>); the default's name rides
/// <c>UI.ChartTemplateDefault</c>.
/// </summary>
/// <remarks>
/// The manager is a PRIMARY-ONLY singleton: the primary chart passes
/// itself in through <see cref="AttachChart"/>, the grid slots pass none
/// and keep their face picks in-session (the T-201 canon). The manager
/// never applies a face directly — every apply is a settings write, and
/// the chart's own changes-stream consumers apply it live (the whole-map
/// writes cannot lose entries to each other, the same discipline the
/// settings window keeps). No dispatcher hop in the manager: the init walk
/// runs pre-binding off-UI harmless, the external-writer and language
/// handlers ride <c>ObserveOnUiThread</c>, and the command-driven
/// mutations rebuild the rows directly.
/// </remarks>
public sealed class ChartTemplatesViewModel : ReactiveObject, IAsyncInitializable
{
    private const string ApplyActionKey = "Chart.Templates.Apply";
    private const string SetDefaultActionKey = "Chart.Templates.SetDefault";
    private const string RenameActionKey = "Chart.Templates.Rename";
    private const string DeleteActionKey = "Chart.Templates.Delete";
    private const string DefaultSuffixKey = "Chart.Templates.DefaultSuffix";
    private const string SavePromptTitleKey = "Chart.Templates.SavePromptTitle";
    private const string SavePromptLabelKey = "Chart.Templates.SavePromptLabel";
    private const string RenamePromptTitleKey = "Chart.Templates.RenamePromptTitle";
    private const string RenamePromptLabelKey = "Chart.Templates.RenamePromptLabel";
    private const string PromptPlaceKey = "Chart.Templates.PromptPlace";
    private const string ApplyNoSymbolKey = "Chart.Templates.ApplyNoSymbol";
    private const string NewerVersionKey = "Chart.Templates.NewerVersion";
    private const string RenameClashKey = "Chart.Templates.RenameClash";
    private const string MutationFailedKey = "Chart.Templates.MutationFailed";

    private readonly ISettingsService _settings;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<ChartTemplatesViewModel> _logger;
    private readonly Subject<ChartTemplate?> _defaultTemplateChanged = new();

    /// <summary>The attached chart source (the primary); null until it hands itself over.</summary>
    private Func<ChartViewModel?>? _chartSource;

    /// <summary>
    /// The current document: the last read or written templates envelope.
    /// The manager adopts every fresh read (the seed, the external writes,
    /// its own mutations) so the rows always mirror the stored truth.
    /// </summary>
    private ChartTemplatesDocument _document = new(ChartTemplatesStore.CurrentVersion, []);

    /// <summary>
    /// The stored default template's name, verbatim — an unknown name
    /// resolves to no default (a ghost pointer never substitutes anything).
    /// </summary>
    private string _defaultName = string.Empty;

    /// <summary>
    /// True while the stored envelope is not this build's version: every
    /// mutation refuses — a NEWER build's file is never clobbered by this
    /// build's next save.
    /// </summary>
    private bool _refusesMutations;

    /// <summary>The last published default (record equality dedupes the publishes).</summary>
    private ChartTemplate? _publishedDefault;

    /// <summary>One template row: the display title plus its four actions.</summary>
    public ObservableCollection<ChartTemplateRow> Rows { get; } = new();

    /// <summary>The save prompt's route (the window answers with the name, or null on cancel).</summary>
    public Interaction<RxVoid, string?> SaveNameRequested { get; } = new();

    /// <summary>The rename prompt's route (the input is the old name, the answer the new one or null).</summary>
    public Interaction<string, string?> RenameRequested { get; } = new();

    /// <summary>
    /// The default template's change stream: publishes the resolved
    /// template (or null when no default stands) whenever it changes — the
    /// chart re-applies the default face to the standing entry-less symbol.
    /// </summary>
    public IObservable<ChartTemplate?> DefaultTemplateChanged => _defaultTemplateChanged;

    /// <summary>
    /// The resolved default template, or null when the stored name points
    /// at nothing usable (the chart's entry-less seeding reads this).
    /// </summary>
    public ChartTemplate? DefaultTemplate =>
        _document.Templates.FirstOrDefault(template =>
            string.Equals(template.Name, _defaultName, StringComparison.Ordinal));

    /// <summary>The templates flyout's save action (the static first item).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>The prompt strings the window's handlers read at call time (refreshed on language change).</summary>
    public string SavePromptTitle { get; private set; } = string.Empty;

    /// <summary>The save prompt's caption.</summary>
    public string SavePromptLabel { get; private set; } = string.Empty;

    /// <summary>The rename prompt's title.</summary>
    public string RenamePromptTitle { get; private set; } = string.Empty;

    /// <summary>The rename prompt's caption.</summary>
    public string RenamePromptLabel { get; private set; } = string.Empty;

    /// <summary>The prompt's place button label (the pair's affirmative).</summary>
    public string PromptPlaceLabel { get; private set; } = string.Empty;

    /// <summary>
    /// Create the manager. The changes-stream and language subscriptions
    /// live for the singleton's lifetime; the init walk fills the document
    /// and the default name from the stored keys.
    /// </summary>
    public ChartTemplatesViewModel(
        ISettingsService settings,
        ILocalizationManager localization,
        ILogger<ChartTemplatesViewModel> logger)
    {
        _settings = settings;
        _localization = localization;
        _logger = logger;

        SaveCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleSaveAsync(cancellationToken));

        // An external writer replaced the whole templates envelope: the
        // fresh parse becomes the truth (rows and refusal guard with it).
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartTemplates)
            .Select(change => change.NewValue)
            .OfType<string>()
            .ObserveOnUiThread()
            .Subscribe(
                ApplyTemplatesDocument,
                ex => _logger.LogError(ex, "The chart templates stream stopped unexpectedly"));

        // An external writer pointed the default elsewhere: the name re-
        // resolves and the resolved template republishes if it changed.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartTemplateDefault)
            .Select(change => change.NewValue)
            .OfType<string>()
            .ObserveOnUiThread()
            .Subscribe(
                ApplyDefaultName,
                ex => _logger.LogError(ex, "The chart default template stream stopped unexpectedly"));

        // The rows' action headers and the prompt strings re-ink on every
        // language change (the dynamic-list discipline the alerts keep).
        _localization.LanguageChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => ApplyLanguageChange(),
                ex => _logger.LogError(ex, "The chart templates language stream stopped unexpectedly"));
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ApplyTemplatesDocument(await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            UiSettingsKeys.DefaultChartTemplates));

        ApplyDefaultName(await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplateDefault,
            UiSettingsKeys.DefaultChartTemplateDefault));

        // The walk runs before the language applies: the prompt strings
        // seed here and re-ink on the language change that follows.
        RefreshPromptStrings();

        _logger.LogInformation(
            "Restored chart templates: {Count} usable, default '{Default}'",
            _document.Templates.Count,
            _document.Templates.Any(template => string.Equals(template.Name, _defaultName, StringComparison.Ordinal))
                ? _defaultName
                : "(none)");
    }

    /// <summary>
    /// Hand the manager the chart whose face is captured and whose error
    /// line reports (the perf HUD's attach-the-consumer shape).
    /// </summary>
    public void AttachChart(Func<ChartViewModel?> chartSource) =>
        _chartSource = chartSource;

    #region Streams

    private void ApplyTemplatesDocument(string? templatesJson) =>
        AdoptDocument(ChartTemplatesStore.Parse(templatesJson));

    private void ApplyDefaultName(string? defaultJson) =>
        ResolveDefault(defaultJson?.Trim() ?? string.Empty);

    private void ApplyLanguageChange()
    {
        RefreshPromptStrings();
        RebuildRows();
    }

    /// <summary>Adopt a parsed document: the rows and the refusal guard follow it, the resolved default republishes if it changed.</summary>
    private void AdoptDocument(ChartTemplatesDocument document)
    {
        _document = document;
        _refusesMutations = document.Version != ChartTemplatesStore.CurrentVersion;
        RebuildRows();
        PublishDefaultChange();
    }

    /// <summary>Re-resolve the default against the stored name; the resolved template republishes if it changed.</summary>
    private void ResolveDefault(string storedName)
    {
        _defaultName = storedName;
        RebuildRows();
        PublishDefaultChange();
    }

    private void PublishDefaultChange()
    {
        var current = DefaultTemplate;
        if (!EqualityComparer<ChartTemplate?>.Default.Equals(_publishedDefault, current))
        {
            _publishedDefault = current;
            _defaultTemplateChanged.OnNext(current);
        }
    }

    private void RefreshPromptStrings()
    {
        SavePromptTitle = _localization.GetString(SavePromptTitleKey);
        SavePromptLabel = _localization.GetString(SavePromptLabelKey);
        RenamePromptTitle = _localization.GetString(RenamePromptTitleKey);
        RenamePromptLabel = _localization.GetString(RenamePromptLabelKey);
        PromptPlaceLabel = _localization.GetString(PromptPlaceKey);
    }

    #endregion

    #region Rows

    private void RebuildRows()
    {
        var suffix = _localization.GetString(DefaultSuffixKey);
        Rows.Clear();
        foreach (var template in _document.Templates)
        {
            // Only the default's title carries the marker: the suffix
            // names the pointer's target, the list never masquerades.
            var title = Title(
                template.Name,
                string.Equals(template.Name, _defaultName, StringComparison.Ordinal) ? suffix : string.Empty);
            Rows.Add(new ChartTemplateRow(title, BuildActions(template)));
        }
    }

    private static string Title(string name, string suffix) =>
        string.IsNullOrEmpty(suffix)
            ? name
            : $"{name} {suffix}".TrimEnd();

    private IReadOnlyList<ChartTemplateAction> BuildActions(ChartTemplate template) =>
    [
        new ChartTemplateAction(
            _localization.GetString(ApplyActionKey),
            ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(_ => HandleApplyAsync(template))),
        new ChartTemplateAction(
            _localization.GetString(SetDefaultActionKey),
            ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(_ => HandleSetDefaultAsync(template))),
        new ChartTemplateAction(
            _localization.GetString(RenameActionKey),
            ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(_ => HandleRenameAsync(template))),
        new ChartTemplateAction(
            _localization.GetString(DeleteActionKey),
            ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(_ => HandleDeleteAsync(template))),
    ];

    #endregion

    #region Mutations

    private async Task<RxVoid> HandleSaveAsync(CancellationToken cancellationToken)
    {
        var chart = _chartSource?.Invoke();
        if (_refusesMutations)
        {
            ReportError(chart, NewerVersionKey);
            return RxVoid.Default;
        }

        var name = (await SaveNameRequested.Handle(RxVoid.Default))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            // A cancelled or empty answer saves nothing (the annotation
            // prompt's grammar).
            return RxVoid.Default;
        }

        var face = chart?.CaptureTemplateFace();
        if (face is null)
        {
            _logger.LogWarning("The chart templates save ran without a capturable chart face; nothing was saved");
            ReportError(chart, MutationFailedKey);
            return RxVoid.Default;
        }

        await MutateCoreAsync(
            chart,
            templates => ReplaceOrAppend(templates, name, ToTemplate(name, face)),
            validate: null);

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleRenameAsync(ChartTemplate template)
    {
        var chart = _chartSource?.Invoke();
        if (_refusesMutations)
        {
            ReportError(chart, NewerVersionKey);
            return RxVoid.Default;
        }

        var newName = (await RenameRequested.Handle(template.Name))?.Trim();
        if (string.IsNullOrEmpty(newName) ||
            string.Equals(newName, template.Name, StringComparison.Ordinal))
        {
            // A cancelled, empty or no-op rename changes nothing.
            return RxVoid.Default;
        }

        var document = await MutateCoreAsync(
            chart,
            templates => templates
                .Select(existing => string.Equals(existing.Name, template.Name, StringComparison.Ordinal)
                    ? existing with { Name = newName }
                    : existing)
                .ToList(),
            validate: candidates => candidates.Any(existing =>
                string.Equals(existing.Name, newName, StringComparison.Ordinal))
                ? RenameClashKey
                : null);
        if (document is null)
        {
            return RxVoid.Default;
        }

        // The default pointer names the template: a rename of the default
        // carries the pointer along (the face itself is untouched).
        if (string.Equals(_defaultName, template.Name, StringComparison.Ordinal))
        {
            _defaultName = newName;
            await WriteDefaultKeyAsync(newName, chart);
            RebuildRows();
            PublishDefaultChange();
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleDeleteAsync(ChartTemplate template)
    {
        var chart = _chartSource?.Invoke();
        if (_refusesMutations)
        {
            ReportError(chart, NewerVersionKey);
            return RxVoid.Default;
        }

        var document = await MutateCoreAsync(
            chart,
            templates => templates
                .Where(existing => !string.Equals(existing.Name, template.Name, StringComparison.Ordinal))
                .ToList(),
            validate: null);
        if (document is null)
        {
            return RxVoid.Default;
        }

        // A deleted default leaves no default standing: the pointer clears
        // with it (the entry-less symbols keep the face the template had
        // applied — the standing choice is never substituted away).
        if (string.Equals(_defaultName, template.Name, StringComparison.Ordinal))
        {
            _defaultName = string.Empty;
            await WriteDefaultKeyAsync(string.Empty, chart);
            RebuildRows();
            PublishDefaultChange();
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> HandleSetDefaultAsync(ChartTemplate template)
    {
        var chart = _chartSource?.Invoke();
        if (_refusesMutations)
        {
            ReportError(chart, NewerVersionKey);
            return RxVoid.Default;
        }

        if (string.Equals(_defaultName, template.Name, StringComparison.Ordinal))
        {
            // Already the default: the pick is idempotent — no write, no
            // publish, the applied-lead discipline keeps.
            return RxVoid.Default;
        }

        // The row's snapshot may stand behind an external document write:
        // the pick validates against a fresh read and persists the fresh
        // record (a vanished template drops the pick instead of pointing
        // the default at a ghost).
        var document = ChartTemplatesStore.Parse(await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            UiSettingsKeys.DefaultChartTemplates));
        if (document.Version != ChartTemplatesStore.CurrentVersion)
        {
            AdoptDocument(document);
            ReportError(chart, NewerVersionKey);
            return RxVoid.Default;
        }

        if (document.Templates.FirstOrDefault(existing =>
                string.Equals(existing.Name, template.Name, StringComparison.Ordinal)) is not { } fresh)
        {
            _logger.LogWarning(
                "The chart template '{Name}' is no longer stored; the default pick was dropped",
                template.Name);
            AdoptDocument(document);
            return RxVoid.Default;
        }

        _defaultName = fresh.Name;
        await WriteDefaultKeyAsync(fresh.Name, chart);
        RebuildRows();
        PublishDefaultChange();

        return RxVoid.Default;
    }

    /// <summary>
    /// The read-transform-write core every envelope mutation rides: a
    /// fresh read, the version guard (a foreign envelope refuses with no
    /// write), the optional pre-transform validation, the write and the
    /// local adoption (rows, guard and default publish follow the stored
    /// truth deterministically — the external echo may not exist in tests).
    /// Null means the mutation refused or failed; nothing was written.
    /// </summary>
    private async Task<ChartTemplatesDocument?> MutateCoreAsync(
        ChartViewModel? chart,
        Func<List<ChartTemplate>, List<ChartTemplate>> transform,
        Func<IReadOnlyList<ChartTemplate>, string?>? validate)
    {
        var document = ChartTemplatesStore.Parse(await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            UiSettingsKeys.DefaultChartTemplates));
        if (document.Version != ChartTemplatesStore.CurrentVersion)
        {
            AdoptDocument(document);
            ReportError(chart, NewerVersionKey);
            return null;
        }

        if (validate?.Invoke(document.Templates) is { } error)
        {
            ReportError(chart, error);
            return null;
        }

        var serialized = ChartTemplatesStore.Serialize(transform(document.Templates.ToList()));
        try
        {
            await _settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, serialized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chart templates document");
            ReportError(chart, MutationFailedKey);
            return null;
        }

        AdoptDocument(ChartTemplatesStore.Parse(serialized));
        return _document;
    }

    private async Task WriteDefaultKeyAsync(string name, ChartViewModel? chart)
    {
        try
        {
            await _settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the default chart template name");
            ReportError(chart, MutationFailedKey);
        }
    }

    private static List<ChartTemplate> ReplaceOrAppend(
        List<ChartTemplate> templates,
        string name,
        ChartTemplate replacement)
    {
        var index = templates.FindIndex(existing =>
            string.Equals(existing.Name, name, StringComparison.Ordinal));
        if (index >= 0)
        {
            templates[index] = replacement;
        }
        else
        {
            templates.Add(replacement);
        }

        return templates;
    }

    private static ChartTemplate ToTemplate(string name, ChartTemplateFace face) =>
        new(
            name,
            face.ChartType,
            face.ScaleMode,
            face.IndicatorsJson,
            face.ShowVwap,
            face.ShowVolumeProfile);

    #endregion

    #region Apply

    /// <summary>
    /// Apply a saved face to the workspace symbol: the per-symbol facets
    /// (type, scale mode) ride a read-modify-write of the charts' own
    /// maps, the global facets (indicators, the two overlays) ride their
    /// own keys verbatim — the chart's changes-stream consumers apply
    /// everything live, the standing symbol re-addresses on the fresh
    /// documents, and the pane heights stay untouched.
    /// </summary>
    private async Task<RxVoid> HandleApplyAsync(ChartTemplate template)
    {
        var chart = _chartSource?.Invoke();
        if (chart?.SelectedSymbol is not { } symbol)
        {
            // The per-symbol facets need a workspace symbol to address:
            // without one the apply refuses as one act.
            ReportError(chart, ApplyNoSymbolKey);
            return RxVoid.Default;
        }

        try
        {
            var types = SymbolChartTypeMap.Parse(await _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolTypes,
                UiSettingsKeys.DefaultChartSymbolTypes));
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolTypes,
                SymbolChartTypeMap.Serialize(
                    SymbolChartTypeMap.WithEntry(types, symbol.Normalized, template.ChartType)));

            var scales = SymbolScaleModeMap.Parse(await _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolScales,
                UiSettingsKeys.DefaultChartSymbolScales));
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolScales,
                SymbolScaleModeMap.Serialize(
                    SymbolScaleModeMap.WithEntry(scales, symbol.Normalized, template.ScaleMode)));

            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                template.IndicatorsJson);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartShowVwap,
                template.ShowVwap);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartShowVolumeProfile,
                template.ShowVolumeProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the chart template '{Name}'", template.Name);
            ReportError(chart, MutationFailedKey);
        }

        return RxVoid.Default;
    }

    #endregion

    #region Reporting

    private void ReportError(ChartViewModel? chart, string errorKey)
    {
        var message = _localization.GetString(errorKey);
        chart?.ReportTemplateError(message);
        _logger.LogWarning("The chart templates manager reported: {Message}", message);
    }

    #endregion
}

/// <summary>
/// The chart face a template captures (T-202): the chart ViewModel's
/// current facet values at save time. Applying writes exactly these —
/// nothing else (the pane heights, the drawings, the symbol and the time
/// frame stay the session's own).
/// </summary>
/// <param name="ChartType">The current rendering type.</param>
/// <param name="ScaleMode">The current Y-axis scale mode.</param>
/// <param name="IndicatorsJson">The current <c>UI.ChartIndicators</c> document, verbatim.</param>
/// <param name="ShowVwap">The current VWAP overlay visibility.</param>
/// <param name="ShowVolumeProfile">The current volume-profile overlay visibility.</param>
public sealed record ChartTemplateFace(
    ChartSeriesType ChartType,
    PriceScaleMode ScaleMode,
    string IndicatorsJson,
    bool ShowVwap,
    bool ShowVolumeProfile);

/// <summary>
/// One template row of the flyout: the display title (the name, with the
/// default marker when the template is the default) and the four actions
/// every row carries.
/// </summary>
/// <param name="Title">The localized display title.</param>
/// <param name="Actions">The row's submenu actions, in flyout order.</param>
public sealed record ChartTemplateRow(string Title, IReadOnlyList<ChartTemplateAction> Actions);

/// <summary>
/// One per-row action: the localized header and the command closing over
/// the manager and the row's template.
/// </summary>
/// <param name="Header">The localized submenu header.</param>
/// <param name="Command">The action's command.</param>
public sealed record ChartTemplateAction(string Header, ReactiveCommand<RxVoid, RxVoid> Command);
