namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Input;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// One row of the cheat-sheet overlay (T-102): the action's localized
/// name and its live (effective) gesture. Rebuilt on every map change and
/// every language switch — the sheet always mirrors what the window
/// actually routes.
/// </summary>
/// <param name="Name">The localized display name.</param>
/// <param name="Gesture">The effective gesture, canonical.</param>
public sealed record HotkeySheetRow(string Name, string Gesture);

/// <summary>
/// The hotkey manager's ViewModel (T-102): the settings tab's editable
/// rows (one per registry action, conflicts and invalid forms diagnosed
/// at edit time, overrides persisted per row and applied without a
/// restart through the service's live map) and the "?" cheat-sheet
/// overlay's mirror of the live map. Every verdict, name and message is
/// localized; a language switch re-runs the verdicts and rebuilds the
/// sheet.
/// </summary>
public sealed class HotkeyViewModel : ReactiveObject
{
    private readonly HotkeyService _service;

    private readonly ILocalizationManager _localization;

    private readonly ILogger<HotkeyViewModel> _logger;

    /// <summary>The rows' last-committed (or seeded) canonical gestures — the conflict scan's baseline.</summary>
    private IReadOnlyDictionary<string, string> _effective;

    private bool _isSheetVisible;

    /// <summary>The menu item's advertised gesture (settings).</summary>
    private string _settingsGesture;

    /// <summary>The menu item's advertised gesture (new order).</summary>
    private string _newOrderGesture;

    /// <summary>The menu item's advertised gesture (AI chat).</summary>
    private string _aiChatGesture;

    /// <summary>The menu item's advertised gesture (palette).</summary>
    private string _paletteGesture;

    /// <summary>
    /// Create a new HotkeyViewModel instance.
    /// </summary>
    /// <param name="service">The hotkey map service (the live map and its persistence).</param>
    /// <param name="localization">The localization manager (the action names and verdicts).</param>
    /// <param name="logger">The ViewModel logger.</param>
    public HotkeyViewModel(
        HotkeyService service,
        ILocalizationManager localization,
        ILogger<HotkeyViewModel> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Rows = [.. HotkeyRegistry.Actions.Select(action => new HotkeyRowViewModel(
            action,
            service.GestureFor(action.Id),
            localization.GetString(action.NameKey)))];
        _settingsGesture = service.GestureFor(HotkeyRegistry.OpenSettings);
        _newOrderGesture = service.GestureFor(HotkeyRegistry.NewOrder);
        _aiChatGesture = service.GestureFor(HotkeyRegistry.ToggleAiChat);
        _paletteGesture = service.GestureFor(HotkeyRegistry.TogglePalette);
        _effective = HotkeyRegistry.Actions.ToDictionary(
            action => action.Id,
            action => service.GestureFor(action.Id),
            StringComparer.Ordinal);

        // The per-row live route (T-102): one subscription per row — every
        // keystroke diagnoses at edit time (the verdict shows while typing)
        // and a valid text commits at once, so the acceptance's apply
        // without restart rides the service's live map with no apply button.
        // Skip(1) is the point: WhenAnyValue emits the seed value once, and
        // committing the map's own truth would persist every default as an
        // override — only a user edit commits.
        foreach (var row in Rows)
        {
            row.WhenAnyValue(x => x.GestureText)
                .Skip(1)
                .Subscribe(
                    _ => CommitRow(row),
                    ex => _logger.LogError(ex, "The hotkey edit stream for {Action} stopped unexpectedly", row.Action.Id));
        }

        // The live map re-seeds the rows, the menu gestures and the sheet —
        // on the UI thread (the map arrives from the settings stream).
        service.MapChanged
            .ObserveOnUiThread()
            .Subscribe(
                ApplyMap,
                ex => _logger.LogError(ex, "The hotkey map stream stopped unexpectedly"));

        // A language switch re-localizes the sheet and the rows' verdicts.
        localization.LanguageChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => Relocalize(),
                ex => _logger.LogError(ex, "The hotkey language stream stopped unexpectedly"));

        ToggleSheetCommand = ReactiveCommand.Create<RxVoid>(_ => { IsSheetVisible = !IsSheetVisible; });
        ResetRowCommand = ReactiveCommand.Create<HotkeyRowViewModel>(ResetRow);

        // The construction-time seed: the service's seed read fired before
        // this ViewModel existed (nothing subscribed to the map stream
        // then), so the first application rides the hand seed here and
        // every surface — the rows, the menu gestures, the cheat sheet —
        // lands from the CURRENT map, synchronously. Every later emission
        // re-applies through the scheduled subscription (a redundant seed
        // is all no-ops under RaiseAndSetIfChanged).
        ApplyMap(service.Map);
    }

    /// <summary>Gets the settings tab's editable rows, in the registry (display) order.</summary>
    public IReadOnlyList<HotkeyRowViewModel> Rows { get; }

    /// <summary>Gets the cheat-sheet's rows (the live map, localized), in the registry order.</summary>
    public IReadOnlyList<HotkeySheetRow> Sheet { get; private set; } = [];

    /// <summary>Gets or sets a value indicating whether the cheat-sheet overlay shows.</summary>
    public bool IsSheetVisible
    {
        get => _isSheetVisible;
        set => this.RaiseAndSetIfChanged(ref _isSheetVisible, value);
    }

    /// <summary>Gets the settings item's advertised gesture (the menu binding).</summary>
    public string SettingsGesture => _settingsGesture;

    /// <summary>Gets the new-order item's advertised gesture (the menu binding).</summary>
    public string NewOrderGesture => _newOrderGesture;

    /// <summary>Gets the AI-chat item's advertised gesture (the menu binding).</summary>
    public string AiChatGesture => _aiChatGesture;

    /// <summary>Gets the palette item's advertised gesture (the menu binding).</summary>
    public string PaletteGesture => _paletteGesture;

    /// <summary>Gets the sheet overlay's toggle (the menu's "?" item).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleSheetCommand { get; }

    /// <summary>Gets the editor row's reset (the tab's per-row "back to default" button).</summary>
    public ReactiveCommand<HotkeyRowViewModel, RxVoid> ResetRowCommand { get; }

    /// <summary>
    /// The main window's key route: the action the pressed combination is
    /// bound to, resolved against the service's live map. The view
    /// translates keys through this one seam — the same map the menu
    /// gestures print and the cheat sheet lists.
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The pressed modifiers.</param>
    /// <param name="actionId">The bound action's id, valid when true.</param>
    public bool TryMatch(Key key, KeyModifiers modifiers, out string actionId) =>
        _service.TryMatch(key, modifiers, out actionId);

    /// <summary>
    /// Commit one row's edit: the verdict runs first (unparseable, plain
    /// and conflicting texts diagnose and never persist), a valid text
    /// persists through the service — the map's re-application re-seeds
    /// the row.
    /// </summary>
    /// <param name="row">The row committing its edit.</param>
    public void CommitRow(HotkeyRowViewModel row)
    {
        if (Diagnose(row))
        {
            return;
        }

        var canonical = HotkeyConflicts.Normalize(row.GestureText)!;

        // The no-op write guard: a text the persisted state already
        // answers (a typed-then-undone edit over a default row, a
        // re-seed emission of an action's own override) must not write
        // again — only a real change persists. The rule is equality
        // against what the store already holds (the override when the
        // action carries one, the map's effective value otherwise); the
        // re-seed emission after every applied document lands on it, so
        // the write-through echo and the reset round trip both settle
        // writes instead of re-persisting forever.
        var persisted = _service.Overrides.TryGetValue(row.Action.Id, out var existing)
            ? existing
            : _effective[row.Action.Id];
        if (canonical == persisted)
        {
            row.SetMessage(string.Empty);
            return;
        }

        Observable.FromAsync(() => _service.SetOverrideAsync(row.Action.Id, canonical))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to persist the hotkey override for {Action}", row.Action.Id));
    }

    /// <summary>
    /// Reset one row to its registry default: the override clears (null
    /// persists as a cleared entry) and the map's re-application re-seeds
    /// the row.
    /// </summary>
    /// <param name="row">The row resetting.</param>
    public void ResetRow(HotkeyRowViewModel row)
    {
        row.SetMessage(string.Empty);
        Observable.FromAsync(() => _service.SetOverrideAsync(row.Action.Id, null))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to clear the hotkey override for {Action}", row.Action.Id));
    }

    /// <summary>
    /// One map application: the rows re-seed (text, override marker), the
    /// menu gestures re-publish, the sheet rebuilds. Also the baseline
    /// for the edit-time conflict scans.
    /// </summary>
    /// <param name="map">The effective map (every registry action covered).</param>
    private void ApplyMap(IReadOnlyDictionary<string, string> map)
    {
        _effective = map;
        foreach (var row in Rows)
        {
            row.GestureText = map.TryGetValue(row.Action.Id, out var gesture) ? gesture : row.Action.DefaultGesture.ToString();
            row.IsOverridden = _service.Overrides.ContainsKey(row.Action.Id);
        }

        this.RaiseAndSetIfChanged(ref _settingsGesture, map[HotkeyRegistry.OpenSettings], nameof(SettingsGesture));
        this.RaiseAndSetIfChanged(ref _newOrderGesture, map[HotkeyRegistry.NewOrder], nameof(NewOrderGesture));
        this.RaiseAndSetIfChanged(ref _aiChatGesture, map[HotkeyRegistry.ToggleAiChat], nameof(AiChatGesture));
        this.RaiseAndSetIfChanged(ref _paletteGesture, map[HotkeyRegistry.TogglePalette], nameof(PaletteGesture));
        RebuildSheet(map);
    }

    /// <summary>
    /// The edit-time verdict for one row: the message sets (or clears) and
    /// true returns when the row must NOT persist — unparseable text, a
    /// modifier-less gesture, or a gesture another action already holds.
    /// </summary>
    /// <param name="row">The row diagnosing its text.</param>
    private bool Diagnose(HotkeyRowViewModel row)
    {
        var canonical = HotkeyConflicts.Normalize(row.GestureText);
        if (canonical is null)
        {
            row.SetMessage(_localization.GetString("Hotkeys.Editor.Invalid"));
            return true;
        }

        if (HotkeyConflicts.IsPlain(canonical))
        {
            row.SetMessage(_localization.GetString("Hotkeys.Editor.Plain"));
            return true;
        }

        var proposed = _effective
            .Select(pair => (Id: pair.Key, Gesture: pair.Key == row.Action.Id ? canonical : pair.Value))
            .ToList();
        if (HotkeyConflicts.FindDuplicateGesture(proposed) is { } duplicate)
        {
            var otherId = duplicate.FirstId == row.Action.Id ? duplicate.SecondId : duplicate.FirstId;
            var otherName = _localization.GetString(HotkeyRegistry.ActionFor(otherId)!.NameKey);
            row.SetMessage(_localization.GetString("Hotkeys.Editor.Conflict", otherName));
            return true;
        }

        row.SetMessage(string.Empty);
        return false;
    }

    /// <summary>Rebuild the sheet from the map, in the registry order, localized.</summary>
    /// <param name="map">The effective map.</param>
    private void RebuildSheet(IReadOnlyDictionary<string, string> map) =>
        Sheet = [.. HotkeyRegistry.Actions.Select(action =>
            new HotkeySheetRow(
                _localization.GetString(action.NameKey),
                map.TryGetValue(action.Id, out var gesture) ? gesture : action.DefaultGesture.ToString()))];

    /// <summary>A language switch: the rows' names and verdicts re-localize and the sheet rebuilds.</summary>
    private void Relocalize()
    {
        foreach (var row in Rows)
        {
            row.SetName(_localization.GetString(row.Action.NameKey));
            if (row.HasMessage)
            {
                Diagnose(row);
            }
        }

        RebuildSheet(_effective);
    }
}
