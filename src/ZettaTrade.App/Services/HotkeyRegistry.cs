namespace ZettaTrade.App.Services;

using Avalonia.Input;

/// <summary>
/// One registrable hotkey action: a stable identity, the resx key of its
/// localized display name and the gesture the app ships with. The catalog
/// is the single seam of T-102 — the window's key route, the menu items'
/// advertised gestures, the cheat-sheet overlay and the settings tab all
/// read it, so advertised and real never diverge.
/// </summary>
/// <param name="Id">The stable action id (also the overrides map's key).</param>
/// <param name="NameKey">The resx key of the localized display name.</param>
/// <param name="DefaultGesture">The shipped gesture, in canonical form.</param>
public sealed record HotkeyAction(string Id, string NameKey, KeyGesture DefaultGesture);

/// <summary>
/// The catalog of hotkey actions (T-102). The gestures are built from
/// <see cref="KeyGesture"/> objects and carried in their canonical string
/// form, so every consumer compares and prints the same spelling the
/// parser round-trips.
/// </summary>
public static class HotkeyRegistry
{
    /// <summary>The action id of opening the settings window.</summary>
    public const string OpenSettings = "Settings.Open";

    /// <summary>The action id of opening the new-order dialog.</summary>
    public const string NewOrder = "Order.New";

    /// <summary>The action id of toggling the AI chat pane.</summary>
    public const string ToggleAiChat = "AiChat.Toggle";

    /// <summary>The action id of opening the command palette.</summary>
    public const string TogglePalette = "Palette.Toggle";

    /// <summary>The action id of the kill switch (cancel all + close all).</summary>
    public const string KillSwitch = "Trading.KillSwitch";

    /// <summary>The shipped settings gesture (canonical by construction).</summary>
    private static readonly KeyGesture OpenSettingsGesture = new(Key.OemComma, KeyModifiers.Control);

    /// <summary>The shipped new-order gesture (canonical by construction).</summary>
    private static readonly KeyGesture NewOrderGesture = new(Key.N, KeyModifiers.Control);

    /// <summary>The shipped AI-chat gesture (canonical by construction).</summary>
    private static readonly KeyGesture ToggleAiChatGesture = new(Key.A, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>The shipped palette gesture (canonical by construction).</summary>
    private static readonly KeyGesture TogglePaletteGesture = new(Key.K, KeyModifiers.Control);

    /// <summary>
    /// The shipped kill-switch gesture (canonical by construction): a
    /// deliberate two-modifier chord far from the reading-writing keys — a
    /// panic action must never fire from a stray stroke.
    /// </summary>
    private static readonly KeyGesture KillSwitchGesture = new(Key.X, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Gets the registry's actions, in the display (cheat sheet) order.</summary>
    public static IReadOnlyList<HotkeyAction> Actions { get; } =
    [
        new(OpenSettings, "Hotkeys.Action.OpenSettings", OpenSettingsGesture),
        new(NewOrder, "Hotkeys.Action.NewOrder", NewOrderGesture),
        new(KillSwitch, "Hotkeys.Action.KillSwitch", KillSwitchGesture),
        new(ToggleAiChat, "Hotkeys.Action.ToggleAiChat", ToggleAiChatGesture),
        new(TogglePalette, "Hotkeys.Action.TogglePalette", TogglePaletteGesture),
    ];

    /// <summary>
    /// The registry action with the given id, or null when the id is
    /// unknown (a stored override naming a retired action).
    /// </summary>
    /// <param name="id">The action id to look up.</param>
    public static HotkeyAction? ActionFor(string id) =>
        Actions.FirstOrDefault(action => action.Id == id);
}
