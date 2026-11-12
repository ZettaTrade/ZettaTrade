namespace ZettaTrade.App.Tests.ViewModels;

using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using FluentAssertions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;

/// <summary>
/// The hotkey manager ViewModel's pins (T-102): the rows seed from the
/// registry in display order with localized names and the live map's
/// gestures, the four advertised menu gestures publish from the same map,
/// a valid edit commits at once and re-seeds every surface through the
/// applied map (apply without restart), an invalid/plain/conflicting edit
/// diagnoses at edit time and persists nothing, a reverted edit
/// manufactures no override, the reset clears, the cheat sheet mirrors
/// the map localized, and a language switch re-localizes the surface.
/// The facts ride the Avalonia dispatcher serial lane: the map mirror is
/// scheduled (ObserveOnUiThread), so every emission push ends in an
/// explicit <c>DrainAsync</c> — and nothing else waits on the dispatcher.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class HotkeyViewModelTests
{
    static HotkeyViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    #region Facts: the seeded surfaces

    [Fact]
    public void TheRows_SeedFromTheRegistryInDisplayOrder_WithLocalizedNames()
    {
        var kit = NewKit();

        kit.ViewModel.Rows.Should().HaveCount(HotkeyRegistry.Actions.Count);
        kit.ViewModel.Rows.Select(row => row.Action.Id).Should().BeEquivalentTo(
            ["Settings.Open", "Order.New", "Trading.KillSwitch", "AiChat.Toggle", "Palette.Toggle"]);
        kit.ViewModel.Rows[2].Action.Id.Should().Be(HotkeyRegistry.KillSwitch,
            "the panic action sits between the everyday order and chat rows in the display order");
        kit.ViewModel.Rows[2].GestureText.Should().Be("Ctrl+Shift+X",
            "the seed gesture is the live map's own truth");
        kit.ViewModel.Rows[0].Name.Should().Be("Hotkeys.Action.OpenSettings",
            "the echoing localizer answers the display name as the resource key");
        kit.ViewModel.Rows[0].GestureText.Should().Be("Ctrl+OemComma",
            "the seed gesture is the live map's own truth");
        kit.ViewModel.Rows.Should().OnlyContain(row => !row.IsOverridden,
            "a fresh install carries no overrides");
        kit.ViewModel.Rows.Should().OnlyContain(row => !row.HasMessage,
            "a seeded row shows no verdict");
    }

    [Fact]
    public void TheMenuGestures_PublishTheEffectiveMap()
    {
        var kit = NewKit();

        kit.ViewModel.SettingsGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+OemComma", "the menu gesture is the typed parse of the map's canonical text");
        kit.ViewModel.NewOrderGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+N");
        kit.ViewModel.AiChatGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+Shift+A");
        kit.ViewModel.KillSwitchGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+Shift+X", "the menu prints the live map's kill-switch chord");
        kit.ViewModel.PaletteGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+K");
    }

    [Fact]
    public void TryMatch_RoutesThroughTheViewModel()
    {
        var kit = NewKit();

        kit.ViewModel.TryMatch(Key.N, KeyModifiers.Control, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.NewOrder);
        kit.ViewModel.TryMatch(Key.B, KeyModifiers.Control, out _).Should().BeFalse(
            "an unbound combination routes nothing");
    }

    [Fact]
    public async Task TheSheet_MirrorsTheLiveMapLocalized()
    {
        var kit = NewKit();
        // The construction-time seed applies the map synchronously in the
        // ViewModel's ctor — the sheet exists as soon as the kit does; the
        // drain stays a no-op formality for the scheduled mirror.
        await AvaloniaDispatcherPump.DrainAsync();

        kit.ViewModel.Sheet.Should().HaveCount(HotkeyRegistry.Actions.Count);
        kit.ViewModel.Sheet.Select(row => row.Name).Should().BeEquivalentTo(
            ["Hotkeys.Action.OpenSettings", "Hotkeys.Action.NewOrder", "Hotkeys.Action.KillSwitch", "Hotkeys.Action.ToggleAiChat", "Hotkeys.Action.TogglePalette"]);
        kit.ViewModel.Sheet.Select(row => row.Gesture).Should().BeEquivalentTo(
            ["Ctrl+OemComma", "Ctrl+N", "Ctrl+Shift+X", "Ctrl+Shift+A", "Ctrl+K"]);
    }

    [Fact]
    public async Task TheSheetToggle_FlipsTheVisibility()
    {
        var kit = NewKit();

        await kit.ViewModel.ToggleSheetCommand.Execute(RxVoid.Default);
        kit.ViewModel.IsSheetVisible.Should().BeTrue("the Help menu's '?' opened the sheet");

        await kit.ViewModel.ToggleSheetCommand.Execute(RxVoid.Default);
        kit.ViewModel.IsSheetVisible.Should().BeFalse("the second press closed the sheet");
    }

    #endregion

    #region Facts: edit, verdict, persist

    [Fact]
    public async Task AValidEdit_CommitsAppliesAndReSeedsEverySurface()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);

        row.GestureText = "Ctrl+B";
        await AvaloniaDispatcherPump.DrainAsync();

        kit.SavedDocuments.Should().ContainSingle()
            .Which.Should().Contain("\"Order.New\":\"Ctrl+B\"", "a valid edit persists at once");

        kit.ApplyDocument(kit.SavedDocuments.Single());
        await AvaloniaDispatcherPump.DrainAsync();

        row.GestureText.Should().Be("Ctrl+B", "the applied map re-seeds the row");
        row.IsOverridden.Should().BeTrue("the row carries a user override now");
        row.HasMessage.Should().BeFalse("a committed row clears its verdict");
        kit.ViewModel.NewOrderGesture.Should().BeOfType<KeyGesture>()
            .Which.ToString().Should().Be("Ctrl+B", "the menu gesture re-publishes live");
        kit.ViewModel.Sheet.Single(item => item.Name == HotkeyRegistry.ActionFor(HotkeyRegistry.NewOrder)!.NameKey)
            .Gesture.Should().Be("Ctrl+B", "the cheat sheet mirrors the applied map");
        kit.ViewModel.TryMatch(Key.B, KeyModifiers.Control, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.NewOrder, "the key route rebinds without a restart");
    }

    [Fact]
    public async Task AnUnparseableEdit_DiagnosesAndPersistsNothing()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);

        row.GestureText = "What!";
        await AvaloniaDispatcherPump.DrainAsync();

        row.Message.Should().Be("Hotkeys.Editor.Invalid");
        row.HasMessage.Should().BeTrue();
        kit.SavedDocuments.Should().BeEmpty("the unrecognized text never persists");
    }

    [Fact]
    public async Task APlainEdit_DiagnosesAndPersistsNothing()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);

        row.GestureText = "B";
        await AvaloniaDispatcherPump.DrainAsync();

        row.Message.Should().Be("Hotkeys.Editor.Plain");
        kit.SavedDocuments.Should().BeEmpty();
    }

    [Fact]
    public async Task AConflictingEdit_DiagnosesTheOwnerAndPersistsNothing()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);

        row.GestureText = "Ctrl+K";
        await AvaloniaDispatcherPump.DrainAsync();

        row.Message.Should().Be("[Hotkeys.Editor.Conflict:Hotkeys.Action.TogglePalette]",
            "the verdict names the other action as its honestly shared gesture's owner");
        kit.SavedDocuments.Should().BeEmpty();
    }

    [Fact]
    public async Task ARevertedEdit_ManufacturesNoOverrideOutOfTheDefault()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);

        row.GestureText = "Ctrl+";
        row.GestureText = "Ctrl+N";
        await AvaloniaDispatcherPump.DrainAsync();

        kit.SavedDocuments.Should().BeEmpty(
            "typing then undoing over a default row persists nothing — only a real change does");
        row.IsOverridden.Should().BeFalse();
        row.HasMessage.Should().BeFalse("the final valid text clears the intermediate verdict");
    }

    [Fact]
    public async Task Reset_ClearsTheOverrideThroughAPersistedNull()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);
        row.GestureText = "Ctrl+Shift+O";
        kit.ApplyDocument(kit.SavedDocuments.Single());
        await AvaloniaDispatcherPump.DrainAsync();

        await kit.ViewModel.ResetRowCommand.Execute(row);
        await AvaloniaDispatcherPump.DrainAsync();

        kit.SavedDocuments.Should().HaveCount(2);
        kit.SavedDocuments[1].Should().Be("{}");

        kit.ApplyDocument(kit.SavedDocuments[1]);
        await AvaloniaDispatcherPump.DrainAsync();

        row.GestureText.Should().Be("Ctrl+N", "the reset hands the row back its registry default");
        row.IsOverridden.Should().BeFalse();
        kit.ViewModel.TryMatch(Key.N, KeyModifiers.Control, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.NewOrder, "the default route is live again");
    }

    [Fact]
    public async Task ALanguageSwitch_ReLocalizesNamesSheetAndVerdicts()
    {
        var kit = NewKit();
        var row = kit.ViewModel.Rows.Single(item => item.Action.Id == HotkeyRegistry.NewOrder);
        row.GestureText = "What!";
        await AvaloniaDispatcherPump.DrainAsync();
        row.Message.Should().Be("Hotkeys.Editor.Invalid", "the edit-time verdict is the baseline");

        kit.Localization
            .GetString(Arg.Any<string>())
            .Returns(call => "ru:" + call.ArgAt<string>(0));
        kit.Localization
            .GetString(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => "[ru:" + call.ArgAt<string>(0) + "]");
        kit.LanguageChanged.OnNext("ru");
        await AvaloniaDispatcherPump.DrainAsync();

        row.Name.Should().Be("ru:Hotkeys.Action.NewOrder");
        row.Message.Should().Be("ru:Hotkeys.Editor.Invalid",
            "a shown verdict re-runs its diagnosis in the active language");
        kit.ViewModel.Sheet.First().Name.Should().Be("ru:Hotkeys.Action.OpenSettings");
    }

    #endregion

    /// <summary>The kit's builder referenced through the test's own namespace (the shared real pair).</summary>
    private static HotkeyKit NewKit() => HotkeyKits.CreateKit();
}
