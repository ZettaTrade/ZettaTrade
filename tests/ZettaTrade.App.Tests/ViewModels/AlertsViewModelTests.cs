namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The alert manager view model's facts (T-096): every draft field parses
/// explicitly (garbage spellings refuse with their own localized line and
/// the engine is never reached), a valid create/update/snooze/delete rides
/// the engine and the manager re-renders from the roster event the engine
/// publishes, the selection loads the editor, a language switch re-labels
/// the pickers and re-renders the rows with the selection surviving, and
/// the attach handle's dispose ends the mirrors. Facts run inside the
/// AvaloniaDispatcherSerial collection and settle through the pump.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class AlertsViewModelTests
{
    static AlertsViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The alert definition one identity-constant fixture preloads.</summary>
    private static readonly Guid FixtureId = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");

    /// <summary>The localized-text fake: keys read as bracketed ids, formats name their arguments in order.</summary>
    private sealed class FakeLocalization : ILocalizationManager
    {
        public Subject<string> Changes { get; } = new();

        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } =
        [
            new LanguageInfo("en", "English", "English", true),
        ];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key) => "[" + key + "]";

        public string GetString(string key, params object[] args) =>
            "[" + key + "/" + string.Join("|", args) + "]";

        public IObservable<string> LanguageChanged => Changes;

        public IObservable<string> GetObservable(string key) => Observable.Empty<string>();
    }

    /// <summary>
    /// The alert-engine fake: the roster is a plain list the tests preload,
    /// the events ride one subject, and the operations are the engine's own
    /// semantics in miniature (validate, mutate, publish a roster change).
    /// </summary>
    private sealed class FakeEngine : IAlertEngine
    {
        public readonly List<AlertDefinition> Roster = [];

        public readonly Subject<AlertEvent> Feed = new();

        public IReadOnlyList<AlertDefinition> Definitions => Roster;

        public IObservable<AlertEvent> Events => Feed;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<AlertDefinition> CreateAsync(AlertDraft draft, CancellationToken ct = default)
        {
            draft.Validate();
            var created = draft.ToCreated(Base);
            Roster.Add(created);
            Feed.OnNext(new AlertEvent(AlertEventKind.RosterChanged, null, null));
            return Task.FromResult(created);
        }

        public Task UpdateAsync(Guid id, AlertDraft draft, CancellationToken ct = default)
        {
            draft.Validate();
            var index = ValidIndex(id);
            Roster[index] = new AlertDefinition(
                id,
                draft.SymbolName,
                draft.Kind,
                draft.Trigger,
                draft.TargetPrice,
                draft.ThresholdPercent,
                draft.WindowMinutes,
                draft.Note,
                AlertStatus.Armed,
                Roster[index].CreatedUtc,
                SnoozedUntilUtc: null,
                LastFiredUtc: null);
            Feed.OnNext(new AlertEvent(AlertEventKind.RosterChanged, null, null));
            return Task.CompletedTask;
        }

        public Task SnoozeAsync(Guid id, CancellationToken ct = default)
        {
            var index = ValidIndex(id);
            Roster[index] = Roster[index] with
            {
                Status = AlertStatus.Snoozed,
                SnoozedUntilUtc = Base.AddMinutes(15),
            };
            Feed.OnNext(new AlertEvent(AlertEventKind.RosterChanged, null, null));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Roster.RemoveAt(ValidIndex(id));
            Feed.OnNext(new AlertEvent(AlertEventKind.RosterChanged, null, null));
            return Task.CompletedTask;
        }

        private int ValidIndex(Guid id)
        {
            var index = Roster.FindIndex(definition => definition.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException($"No alert '{id}' is on the roster; the operation is refused.");
            }

            return index;
        }
    }

    /// <summary>The preloaded fixture: one armed above-level price alert.</summary>
    private static AlertDefinition Fixture() => new(
        Id: FixtureId,
        SymbolName: "BTC/USDT",
        Kind: PriceAlertKind.Price,
        Trigger: AlertTrigger.Above,
        TargetPrice: 110_000.5m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: "watch",
        Status: AlertStatus.Armed,
        CreatedUtc: Base,
        SnoozedUntilUtc: null,
        LastFiredUtc: null);

    /// <summary>Build one model over one fake engine and the bracketed localization.</summary>
    private static (AlertsViewModel Model, FakeEngine Engine, FakeLocalization Loc) Build(
        params AlertDefinition[] preloaded)
    {
        var engine = new FakeEngine();
        engine.Roster.AddRange(preloaded);
        var loc = new FakeLocalization();
        var model = new AlertsViewModel(
            engine,
            loc,
            NullLogger<AlertsViewModel>.Instance);
        return (model, engine, loc);
    }

    #region Draft Refusals

    [Fact]
    public async Task CreateCommand_AGarbageSymbolRefusesWithItsOwnLine_AndNeverReachesTheEngine()
    {
        var (model, engine, _) = Build();
        model.SymbolText = "not a symbol";
        model.TargetText = "110";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidSymbol/not a symbol]");
        model.HasErrorMessage.Should().BeTrue();
        engine.Roster.Should().BeEmpty("a refused draft never reaches the engine");
    }

    [Fact]
    public async Task CreateCommand_AnUnparsableTargetRefuses_ThePriceFieldsCarryTheirOwnLine()
    {
        var (model, engine, _) = Build();
        model.SymbolText = "BTC/USDT";
        model.TargetText = "12.3x";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidTarget/12.3x]");
        engine.Roster.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateCommand_AnUnparsablePercentRefuses_ThePercentFieldsCarryTheirOwnLine()
    {
        var (model, engine, _) = Build();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.PercentMove);
        model.SymbolText = "ETH/USDT";
        model.PercentText = "up a lot";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidPercent/up a lot]");
        engine.Roster.Should().BeEmpty();
    }

    #endregion Draft Refusals

    #region Valid Drafts

    [Fact]
    public async Task CreateCommand_AValidPriceDraft_ReachesTheEngine_AndTheRosterEventRendersTheRow()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SymbolText = "BTC/USDT";
        model.TargetText = "110";

        var created = await model.CreateCommand.Execute(RxVoid.Default);
        created.Should().Be(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        engine.Roster.Should().ContainSingle();
        var definition = engine.Roster[0];
        definition.SymbolName.Should().Be("BTC/USDT", "the composed draft parses through Symbol");
        definition.Kind.Should().Be(PriceAlertKind.Price);
        definition.Trigger.Should().Be(AlertTrigger.Below, "the editor's default edge is the Below option");
        definition.TargetPrice.Should().Be(110m);
        definition.Status.Should().Be(AlertStatus.Armed, "a fresh create is an armed alert");

        model.ErrorMessage.Should().BeNull("a clean create carries no refusal");
        model.SymbolText.Should().BeEmpty("the editor clears the accepted spelling");
        model.Rows.Should().HaveCount(1, "the engine's roster event re-renders the table");
        model.Rows[0].SymbolText.Should().Be("BTC/USDT");
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.Price.Below/110]");
        model.Rows[0].StatusText.Should().Be("[Alert.Status.Armed]");
        model.HasRows.Should().BeTrue();
    }

    [Fact]
    public async Task CreateCommand_AValidPercentDraft_CarriesThePercentFacts()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.PercentMove);
        model.SymbolText = "ETH/USDT";
        model.PercentText = "8";
        model.WindowText = "60";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.Kind.Should().Be(PriceAlertKind.PercentMove);
        definition.ThresholdPercent.Should().Be(8m);
        definition.WindowMinutes.Should().Be(60);
        definition.Trigger.Should().Be(AlertTrigger.Below, "the percent kind leaves the edge slot neutral");
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.PercentMove.Format/8|60]");
    }

    [Fact]
    public void TheEditor_SplitsTheKindsFields()
    {
        var (model, _, _) = Build();
        model.IsPriceKind.Should().BeTrue();
        model.IsPercentKind.Should().BeFalse();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.PercentMove);
        model.IsPriceKind.Should().BeFalse();
        model.IsPercentKind.Should().BeTrue();
    }

    #endregion Valid Drafts

    #region Selection

    [Fact]
    public void SelectingARow_LoadsTheEditor()
    {
        var (model, _, _) = Build(Fixture());

        model.Rows.Should().ContainSingle("the constructor renders the current roster");
        model.SelectedRow = model.Rows[0];

        model.SymbolText.Should().Be("BTC/USDT");
        model.TargetText.Should().Be("110000.5", "the editor loads the definition's exact facts");
        model.SelectedKind.Kind.Should().Be(PriceAlertKind.Price);
        model.SelectedTrigger.Trigger.Should().Be(AlertTrigger.Above);
        model.NoteText.Should().Be("watch");
    }

    [Fact]
    public async Task DeleteSelectedCommand_RemovesTheRow_AndClearsTheSelection()
    {
        var (model, engine, _) = Build(Fixture());
        using var attach = model.Attach();
        model.SelectedRow = model.Rows[0];

        await model.DeleteSelectedCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        engine.Roster.Should().BeEmpty();
        model.Rows.Should().BeEmpty();
        model.HasRows.Should().BeFalse();
        model.SelectedRow.Should().BeNull("the selection's alert is gone: the editor clears");
        model.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SnoozeSelectedCommand_PushesTheWindow_AndTheRowReRendersSnoozed()
    {
        var (model, engine, _) = Build(Fixture());
        using var attach = model.Attach();
        model.SelectedRow = model.Rows[0];

        await model.SnoozeSelectedCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        engine.Roster[0].Status.Should().Be(AlertStatus.Snoozed);
        model.Rows[0].StatusText.Should().StartWith(
            "[Alert.Status.Snoozed.Format/",
            "the row re-renders the snoozed lifecycle from the engine's event");
    }

    [Fact]
    public async Task UpdateSelectedCommand_ReArmsWithTheEditor()
    {
        var (model, engine, _) = Build(Fixture());
        using var attach = model.Attach();
        model.SelectedRow = model.Rows[0];
        model.TargetText = "120";
        model.SelectedTrigger = model.TriggerOptions.First(option => option.Trigger == AlertTrigger.At);

        await model.UpdateSelectedCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var reArmed = engine.Roster.Should().ContainSingle().Subject;
        reArmed.Id.Should().Be(FixtureId, "an edit keeps the identity");
        reArmed.TargetPrice.Should().Be(120m);
        reArmed.Trigger.Should().Be(AlertTrigger.At);
        reArmed.Status.Should().Be(AlertStatus.Armed, "the edit is a re-arm");
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.Price.At/120]");
    }

    #endregion Selection

    #region Attach and Language

    [Fact]
    public async Task TheLanguageSwitch_ReLabelsTheRowsAndPickers_TheSelectionSurvives()
    {
        var (model, _, loc) = Build(Fixture());
        using var attach = model.Attach();
        model.SelectedRow = model.Rows[0];
        var selectedBefore = model.SelectedRow;

        loc.Changes.OnNext("ru");
        await AvaloniaDispatcherPump.DrainAsync();

        model.Rows.Should().ContainSingle();
        model.Rows[0].Should().NotBeSameAs(selectedBefore, "the language switch re-renders the row set");
        model.Rows[0].Id.Should().Be(FixtureId);
        model.Rows[0].StatusText.Should().Be("[Alert.Status.Armed]");
        model.SelectedRow!.Id.Should().Be(FixtureId, "the selection survives the re-render");
        model.SymbolText.Should().Be("BTC/USDT", "the editor re-loads the re-selected row");
        model.KindOptions[0].Label.Should().Be("[Alert.Kind.Price]", "the pickers re-compose their labels");
    }

    [Fact]
    public async Task TheDisposedHandle_EndsTheRosterMirror()
    {
        var (model, engine, _) = Build(Fixture());
        using (var attach = model.Attach())
        {
            await AvaloniaDispatcherPump.DrainAsync();
            model.Rows.Should().ContainSingle();
        }

        // The handle's dispose kills the mirrors: no roster change lands after it.
        engine.Roster.Add(Fixture());
        engine.Feed.OnNext(new AlertEvent(AlertEventKind.RosterChanged, null, null));
        await AvaloniaDispatcherPump.DrainAsync();
        model.Rows.Should().ContainSingle("the disposed attach handle ends the mirror");
    }

    #endregion Attach and Language
}
