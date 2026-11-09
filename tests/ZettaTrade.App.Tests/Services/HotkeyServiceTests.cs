namespace ZettaTrade.App.Tests.Services;

using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using FluentAssertions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The hotkey live map's pins (T-102): the shipped defaults route the four
/// registry gestures, a stored document rebinds (the override replaces the
/// default route), unusable entries drop without poison (unknown ids,
/// non-string values, unparseable gestures, malformed documents), a plain
/// stored gesture stays diagnosed in the map yet OUT of the routes, and
/// the persistence round trip carries the serialized document back in
/// through the changes stream.
/// </summary>
public sealed class HotkeyServiceTests
{
    #region Facts: the shipped map

    [Fact]
    public void TheShippedMap_CarriesTheRegistryDefaults_AndRoutesThem()
    {
        var kit = NewKit();

        kit.Service.Overrides.Should().BeEmpty("a fresh install carries no overrides");
        kit.Service.GestureFor(HotkeyRegistry.OpenSettings).Should().Be("Ctrl+OemComma");
        kit.Service.GestureFor(HotkeyRegistry.NewOrder).Should().Be("Ctrl+N");
        kit.Service.GestureFor(HotkeyRegistry.ToggleAiChat).Should().Be("Ctrl+Shift+A");
        kit.Service.GestureFor(HotkeyRegistry.TogglePalette).Should().Be("Ctrl+K");
    }

    [Fact]
    public void TryMatch_RoutesTheDefaults_AndMissesPlainPresses()
    {
        var kit = NewKit();

        kit.Service.TryMatch(Key.OemComma, KeyModifiers.Control, out var settingsId).Should().BeTrue();
        settingsId.Should().Be(HotkeyRegistry.OpenSettings);
        kit.Service.TryMatch(Key.N, KeyModifiers.Control, out var orderId).Should().BeTrue();
        orderId.Should().Be(HotkeyRegistry.NewOrder);
        kit.Service.TryMatch(Key.A, KeyModifiers.Control | KeyModifiers.Shift, out var chatId).Should().BeTrue();
        chatId.Should().Be(HotkeyRegistry.ToggleAiChat);
        kit.Service.TryMatch(Key.K, KeyModifiers.Control, out var paletteId).Should().BeTrue();
        paletteId.Should().Be(HotkeyRegistry.TogglePalette);

        kit.Service.TryMatch(Key.N, KeyModifiers.None, out _).Should().BeFalse(
            "a modifier-less press never routes (the plain gesture verdict)");
        kit.Service.TryMatch(Key.Q, KeyModifiers.Control, out _).Should().BeFalse(
            "a press outside the map routes nothing");
    }

    #endregion

    #region Facts: the stored document

    [Fact]
    public void TheStoredDocument_RestoresOverrides_AndRebindsTheRoutes()
    {
        var kit = NewKit("""{"Order.New":"Ctrl+B"}""");

        kit.Service.Overrides.Should().BeEquivalentTo(
            new[] { System.Collections.Generic.KeyValuePair.Create(HotkeyRegistry.NewOrder, "Ctrl+B") },
            "the restored document is the persisted overrides");
        kit.Service.GestureFor(HotkeyRegistry.NewOrder).Should().Be("Ctrl+B");
        kit.Service.TryMatch(Key.B, KeyModifiers.Control, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.NewOrder);
        kit.Service.TryMatch(Key.N, KeyModifiers.Control, out _).Should().BeFalse(
            "the override REPLACES the default route — one gesture per action");
    }

    [Fact]
    public void TheStoredDocument_DropsUnusableEntriesWithoutPoison()
    {
        var kit = NewKit(
            """{"Nope.X":"Ctrl+Q","Order.New":42,"Palette.Toggle":"What!","AiChat.Toggle":"Ctrl+Alt+I"}""");

        kit.Service.Overrides.Keys.Should().BeEquivalentTo([HotkeyRegistry.ToggleAiChat],
            "the unknown action id, the non-string value and the unparseable gesture all drop");
        kit.Service.GestureFor(HotkeyRegistry.OpenSettings).Should().Be("Ctrl+OemComma");
        kit.Service.GestureFor(HotkeyRegistry.ToggleAiChat).Should().Be("Ctrl+Alt+I");
    }

    [Fact]
    public void TheStoredDocument_IsFailSafeOnMalformedJson()
    {
        var kit = NewKit("@@not json@@");

        kit.Service.Overrides.Should().BeEmpty("the malformed document degrades to nothing");
        kit.Service.TryMatch(Key.K, KeyModifiers.Control, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.TogglePalette, "the defaults keep routing");
    }

    [Fact]
    public void APlainStoredGesture_StaysDiagnosedInTheMap_AndOutOfTheRoutes()
    {
        var kit = NewKit("""{"Order.New":"B"}""");

        kit.Service.GestureFor(HotkeyRegistry.NewOrder).Should().Be("B",
            "the map keeps the stored value so the editor can show the diagnosed row");
        kit.Service.TryMatch(Key.B, KeyModifiers.None, out _).Should().BeFalse(
            "the routes keep a modifier-less gesture out — a bound plain key would fire while typing");
    }

    #endregion

    #region Facts: persistence and the live reload

    [Fact]
    public async Task SetOverrideAsync_PersistsTheOverridesDocument()
    {
        var kit = NewKit();

        await kit.Service.SetOverrideAsync(HotkeyRegistry.NewOrder, "Ctrl+B");

        kit.SavedDocuments.Should().ContainSingle()
            .Which.Should().Contain("\"Order.New\":\"Ctrl+B\"",
            "the write carries the serialized overrides document");
    }

    [Fact]
    public async Task ClearingAnOverride_PersistsTheShrunkDocument()
    {
        var kit = NewKit();
        await kit.Service.SetOverrideAsync(HotkeyRegistry.NewOrder, "Ctrl+B");

        await kit.Service.SetOverrideAsync(HotkeyRegistry.NewOrder, null);

        kit.SavedDocuments.Should().HaveCount(2);
        kit.SavedDocuments[1].Should().Be("{}",
            "the cleared entry shrinks the document back to nothing");
    }

    [Fact]
    public void TheChangesStream_ReappliesAStoredDocument_Live()
    {
        var kit = NewKit();

        kit.ApplyDocument("""{"Settings.Open":"Ctrl+Shift+O"}""");

        kit.Service.GestureFor(HotkeyRegistry.OpenSettings).Should().Be("Ctrl+Shift+O");
        kit.Service.TryMatch(Key.O, KeyModifiers.Control | KeyModifiers.Shift, out var route).Should().BeTrue();
        route.Should().Be(HotkeyRegistry.OpenSettings);
    }

    [Fact]
    public void TheChangesStream_IgnoresOtherSectionsAndKeys()
    {
        var kit = NewKit();

        kit.Changes.OnNext(new SettingsChange("Diagnostics", "HudEnabled", false, true));
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.HotkeySection,
            UiSettingsKeys.HotkeyOverrides + "Elsewhere",
            "",
            """{"Order.New":"Ctrl+B"}"""));

        kit.Service.GestureFor(HotkeyRegistry.NewOrder).Should().Be("Ctrl+N",
            "only the hotkey document's own section and key re-apply the map");
    }

    [Fact]
    public void TheChangesStream_DropsNonStringValues()
    {
        var kit = NewKit();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.HotkeySection,
            UiSettingsKeys.HotkeyOverrides,
            UiSettingsKeys.DefaultHotkeyOverrides,
            42));

        kit.Service.GestureFor(HotkeyRegistry.NewOrder).Should().Be("Ctrl+N",
            "a non-string document value stands for nothing and drops");
    }

    #endregion

    /// <summary>A fresh kit off the shared ViewModels-builder (the same real pair the VM facts ride).</summary>
    private static ZettaTrade.App.Tests.ViewModels.HotkeyKit NewKit(string? storedDocument = null) =>
        ZettaTrade.App.Tests.ViewModels.HotkeyKits.CreateKit(storedDocument);
}
