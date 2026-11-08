namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The settings port's live re-apply contract (T-100): an applied import
/// lands as one locked batch whose real differences publish exactly one
/// reactive change each on the settings service's change stream — every
/// subscriber that already reacts to settings changes re-applies live, no
/// restart — and the store reads back the imported values at once.
/// </summary>
public sealed class SettingsPortLiveReapplyTests
{
    /// <summary>
    /// An import of a foreign posture publishes one change per real
    /// difference (with old and new payloads sharing the type) and NOTHING
    /// for entries the file describes as already-stored values.
    /// </summary>
    [Fact]
    public async Task AnAppliedImport_PublishesOneChangePerRealDifference()
    {
        using var kit = new SettingsPortKit();
        await kit.Settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, false);
        await kit.Settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.AiPanelWidth, 300);

        var liveChanges = new List<SettingsChange>();
        using var subscription = kit.Settings.Changes.Subscribe(liveChanges.Add);

        var plan = await kit.Port.DraftImportAsync(
            PortDocument(
                "\"UI\": { \"ChartUse12HourClock\": true, \"AiPanelWidth\": 300, \"BookPanelWidth\": 360 }"));
        await kit.Port.ApplyImportAsync(plan);

        liveChanges
            .Select(change => (change.Section, change.Key))
            .Should()
            .BeEquivalentTo(
            [
                (UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock),
                (UiSettingsKeys.Section, UiSettingsKeys.BookPanelWidth),
            ], "exactly the real differences publish — the identical value writes nothing");

        var clockChange = liveChanges.Single(change => change.Key == UiSettingsKeys.ChartUse12HourClock);
        ((JsonElement)clockChange.OldValue!).GetBoolean().Should().BeFalse();
        ((JsonElement)clockChange.NewValue!).GetBoolean().Should().BeTrue();

        // The live re-apply the acceptance asks for: the values are IN the
        // store the moment the batch returns — no restart, no reload wait.
        (await kit.Settings.GetAsync<bool>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartUse12HourClock))
            .Should().BeTrue("the imported choice applies live");
        (await kit.Settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.BookPanelWidth))
            .Should().Be(360);
    }

    /// <summary>
    /// The import file lands on the disk in the same batch: a settings file
    /// an editor later reads already holds the imported values (one save, no
    /// half-written posture).
    /// </summary>
    [Fact]
    public async Task AnAppliedImport_LandsTheFileInOneWrite()
    {
        using var kit = new SettingsPortKit();
        var plan = await kit.Port.DraftImportAsync(
            PortDocument("\"UI\": { \"Theme\": \"trading-pro\", \"ChartUse12HourClock\": true }"));

        await kit.Port.ApplyImportAsync(plan);

        var persisted = await File.ReadAllTextAsync(Path.Combine(kit.Directory, "settings.json"));
        persisted.Should().Contain("trading-pro");
        persisted.Should().Contain("\"ChartUse12HourClock\": true");
    }

    /// <summary>
    /// The subscriber lane stays live across many imports: applying two
    /// plans in sequence publishes each batch's own differences (and nothing
    /// between them).
    /// </summary>
    [Fact]
    public async Task SequentialImports_PublishTheirOwnDifferences()
    {
        using var kit = new SettingsPortKit();

        var liveChanges = new List<SettingsChange>();
        using var subscription = kit.Settings.Changes.Subscribe(liveChanges.Add);

        await kit.Port.ApplyImportAsync(await kit.Port.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": true }")));
        await kit.Port.ApplyImportAsync(await kit.Port.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": false }")));
        await kit.Port.ApplyImportAsync(await kit.Port.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": false }")));

        liveChanges
            .Select(change => change.Key)
            .Should()
            .BeEquivalentTo(
                [UiSettingsKeys.ChartUse12HourClock, UiSettingsKeys.ChartUse12HourClock],
                "each batch publishes its own difference; the second re-import publishes nothing");
    }

    #region Factories

    /// <summary>Wrap a settings payload into a full (schema, version, settings) document.</summary>
    private static string PortDocument(string settingsBody) =>
        $$"""
        {
          "schema": "{{SettingsPortService.SchemaMarker}}",
          "version": {{SettingsPortService.SchemaVersion}},
          "settings": { {{settingsBody}} }
        }
        """;

    #endregion Factories
}
