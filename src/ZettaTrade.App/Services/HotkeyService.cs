namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The hotkey bindings' live map (T-102): the registry's defaults carried
/// by the user's persisted overrides. One construction-time async read
/// seeds the map and every changes-stream emission re-applies it — a
/// settings import or a tab edit lands WITHOUT a restart. The window's
/// key route, the menu items' advertised gestures and the cheat-sheet
/// overlay all read this one map, so advertised and real never diverge.
/// </summary>
public sealed class HotkeyService
{
    private readonly ISettingsService _settings;

    private readonly ILogger<HotkeyService> _logger;

    /// <summary>The persisted overrides (action id to canonical gesture).</summary>
    private IReadOnlyDictionary<string, string> _overrides =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The effective map (every registry action covered); swapped atomically.</summary>
    private IReadOnlyDictionary<string, string> _map = Defaults();

    /// <summary>The key routes (the gesture parts to the action id); rebuilt per map.</summary>
    private IReadOnlyDictionary<(Key Key, KeyModifiers Modifiers), string> _routes = BuildRoutes(Defaults());

    /// <summary>The map's emissions lane; one emission per applied document.</summary>
    private readonly Subject<IReadOnlyDictionary<string, string>> _mapChanged = new();

    /// <summary>Gets the effective map (action id to canonical gesture), every registry action covered.</summary>
    public IReadOnlyDictionary<string, string> Map => _map;

    /// <summary>Gets the persisted overrides (the map's subset that left its default).</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>Gets the map's emissions while the app runs; one per applied document.</summary>
    public IObservable<IReadOnlyDictionary<string, string>> MapChanged => _mapChanged.Synchronize();

    /// <summary>
    /// The service builds its map at construction: one async read seeds
    /// the overrides and the changes stream re-applies every later
    /// document (a tab edit, a settings import, a hot reload).
    /// </summary>
    /// <param name="settings">The settings service (the overrides document's store and changes stream).</param>
    /// <param name="logger">The service logger.</param>
    public HotkeyService(
        ISettingsService settings,
        ILogger<HotkeyService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.HotkeySection,
                UiSettingsKeys.HotkeyOverrides,
                UiSettingsKeys.DefaultHotkeyOverrides))
            .Subscribe(
                ApplyDocument,
                ex => _logger.LogError(ex, "Failed to read the hotkey override map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.HotkeySection &&
                             change.Key == UiSettingsKeys.HotkeyOverrides)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                ApplyDocument,
                ex => _logger.LogError(ex, "The hotkey override map stream stopped unexpectedly"));
    }

    /// <summary>
    /// The effective gesture for an action: the override when set, the
    /// registry default otherwise. Unknown ids answer their registry
    /// lookup (null) — the callers only pass registry ids.
    /// </summary>
    /// <param name="actionId">The registry action id.</param>
    public string GestureFor(string actionId) =>
        _map.TryGetValue(actionId, out var gesture)
            ? gesture
            : HotkeyRegistry.ActionFor(actionId)?.DefaultGesture.ToString() ?? string.Empty;

    /// <summary>
    /// The key route: the action the pressed key combination is bound to,
    /// or false when nothing is. Allocation-free — the lookup rides a
    /// prebuilt route dictionary, never a string parse.
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The pressed modifiers.</param>
    /// <param name="actionId">The bound action's id, valid when true.</param>
    public bool TryMatch(Key key, KeyModifiers modifiers, out string actionId)
    {
        if (_routes.TryGetValue((key, modifiers), out var route))
        {
            actionId = route;
            return true;
        }

        actionId = string.Empty;
        return false;
    }

    /// <summary>
    /// Persist one override (or clear it with null): the cache leads the
    /// pipeline (updated at issue time, in issuance order, so a rapid
    /// burst of edits never drops its second write) and the settings
    /// write lands the document — the map re-applies through the same
    /// changes stream every other consumer rides.
    /// </summary>
    /// <param name="actionId">The registry action id to address.</param>
    /// <param name="canonicalGesture">The override's canonical gesture, or null to clear.</param>
    public async Task SetOverrideAsync(string actionId, string? canonicalGesture)
    {
        _overrides = HotkeyOverrideMap.WithEntry(_overrides, actionId, canonicalGesture);
        await _settings.SetAsync(
            UiSettingsKeys.HotkeySection,
            UiSettingsKeys.HotkeyOverrides,
            HotkeyOverrideMap.Serialize(_overrides));
    }

    /// <summary>Apply one stored document: parse, log the drops, swap the map.</summary>
    /// <param name="json">The stored JSON document.</param>
    private void ApplyDocument(string json)
    {
        _overrides = HotkeyOverrideMap.Parse(json);
        var map = Defaults();
        foreach (var (id, gesture) in _overrides)
        {
            map[id] = gesture;
        }

        var dropped = _overrides.Count;
        _map = map;
        _routes = BuildRoutes(map);
        _mapChanged.OnNext(map);
        _logger.LogDebug(
            "Hotkey map applied: {Overrides} override(s) of {Actions} action(s)",
            dropped,
            HotkeyRegistry.Actions.Count);
    }

    /// <summary>The every-default map: one canonical gesture per registry action.</summary>
    private static Dictionary<string, string> Defaults() =>
        HotkeyRegistry.Actions.ToDictionary(action => action.Id, action => action.DefaultGesture.ToString(), StringComparer.Ordinal);

    /// <summary>
    /// The key routes for a map: each canonical gesture parses once into
    /// its (key, modifiers) pair. A route whose gesture fails to parse
    /// (impossible by construction — every map entry is canonical) drops,
    /// and a modifier-less route drops too: the editor refuses to create
    /// one, but a hand-edited settings file may hold it — the map keeps
    /// it (the editor shows the diagnosed value) while the routes keep it
    /// OUT, so a bound plain key never fires while the user types.
    /// </summary>
    /// <param name="map">The effective map.</param>
    private static IReadOnlyDictionary<(Key Key, KeyModifiers Modifiers), string> BuildRoutes(
        IReadOnlyDictionary<string, string> map)
    {
        Dictionary<(Key Key, KeyModifiers Modifiers), string> routes = new();
        foreach (var (id, gesture) in map)
        {
            var parsed = KeyGesture.Parse(gesture);
            if (parsed.KeyModifiers == KeyModifiers.None)
            {
                continue;
            }

            routes[(parsed.Key, parsed.KeyModifiers)] = id;
        }

        return routes;
    }
}
