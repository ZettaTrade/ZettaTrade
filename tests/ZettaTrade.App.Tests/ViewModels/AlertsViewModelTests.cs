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
using ZettaTrade.Charting;
using ZettaTrade.Plugins.Abstractions;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The alert manager view model's facts (T-096, T-203, T-204): every draft
/// field parses explicitly (garbage spellings refuse with their own
/// localized line and the engine is never reached), a valid
/// create/update/snooze/delete rides the engine and the manager re-renders
/// from the roster event the engine publishes, the selection loads the
/// editor (including the re-arm mode, the cooldown window and the sound
/// choice), a language switch re-labels the pickers and re-renders the rows
/// with the selection surviving, and the attach handle's dispose ends the
/// mirrors. Facts run inside the AvaloniaDispatcherSerial collection and
/// settle through the pump.
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
                LastFiredUtc: null,
                draft.Indicator,
                draft.ReArm,
                draft.CooldownMinutes,
                draft.Sound);
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

    /// <summary>The cross-kind fixture: one RSI(14) indicator-value cross above 70.</summary>
    private static AlertDefinition CrossFixture(
        AlertStatus status = AlertStatus.Armed,
        AlertTrigger trigger = AlertTrigger.Above) => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000002"),
        SymbolName: "BTC/USDT",
        Kind: PriceAlertKind.IndicatorCross,
        Trigger: trigger,
        TargetPrice: 0m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        Status: status,
        CreatedUtc: Base,
        SnoozedUntilUtc: null,
        LastFiredUtc: null,
        Indicator: new AlertIndicatorSpec("RSI", 14, AlertCrossSource.IndicatorValue, 70m));

    /// <summary>The re-arm fixture: one cooldown, silent price alert with a stored five-minute window.</summary>
    private static AlertDefinition ReArmFixture() => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000003"),
        SymbolName: "BTC/USDT",
        Kind: PriceAlertKind.Price,
        Trigger: AlertTrigger.Above,
        TargetPrice: 110_000.5m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        Status: AlertStatus.Armed,
        CreatedUtc: Base,
        SnoozedUntilUtc: null,
        LastFiredUtc: null,
        ReArm: AlertReArmMode.Cooldown,
        CooldownMinutes: 5,
        Sound: AlertSoundChoice.Silent);

    /// <summary>Build one model over one fake engine and the bracketed localization.</summary>
    private static (AlertsViewModel Model, FakeEngine Engine, FakeLocalization Loc) Build(
        params AlertDefinition[] preloaded)
    {
        var engine = new FakeEngine();
        engine.Roster.AddRange(preloaded);
        var loc = new FakeLocalization();
        var model = new AlertsViewModel(
            engine,
            new FakeCatalog(),
            loc,
            NullLogger<AlertsViewModel>.Instance);
        return (model, engine, loc);
    }

    /// <summary>Build one model whose plugin set carries the named indicators (the cross editor's picker surface).</summary>
    private static (AlertsViewModel Model, FakeEngine Engine, FakeLocalization Loc) BuildWithIndicators(
        string[] indicatorNames,
        params AlertDefinition[] preloaded)
    {
        var engine = new FakeEngine();
        engine.Roster.AddRange(preloaded);
        var loc = new FakeLocalization();
        var model = new AlertsViewModel(
            engine,
            new FakeCatalog(indicatorNames),
            loc,
            NullLogger<AlertsViewModel>.Instance);
        return (model, engine, loc);
    }

    /// <summary>The indicator catalog fake: the loaded names the picker reads; the VM never builds implementations.</summary>
    private sealed class FakeCatalog(params string[] names) : IIndicatorCatalog
    {
        public IReadOnlyCollection<string> LoadedNames => names;

        public IIndicator? Create(string name, int period) => null;
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

    [Fact]
    public async Task CreateCommand_AnUnparsableFundingThresholdRefuses_TheFundingFieldsCarryTheirOwnLine()
    {
        var (model, engine, _) = Build();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);
        model.SymbolText = "BTC/USDT:USDT";
        model.TargetText = "half a percent";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidFundingThreshold/half a percent]");
        engine.Roster.Should().BeEmpty();
    }

    /// <summary>A settle mark on a single asset is the malformed shape the parse refuses: the symbol line carries it, never a guess.</summary>
    [Fact]
    public async Task CreateCommand_ASettleMarkedSingleAssetRefuses_WithTheSymbolLine()
    {
        var (model, engine, _) = Build();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);
        model.SymbolText = "USDT:USDT";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidSymbol/USDT:USDT]");
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

    /// <summary>
    /// The funding draft (T-240) rides the price slot in PERCENT units: the
    /// threshold parses, the default edge stays Below, and the row renders
    /// the funding condition's own line. The fact pins an integer threshold
    /// so the rendered line is culture-invariant; a fraction's own parse is
    /// pinned by the codec's signed-threshold fact.
    /// </summary>
    [Fact]
    public async Task CreateCommand_AValidFundingDraft_ReachesTheEngine_InPercentUnits()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);
        model.SymbolText = "BTC/USDT:USDT";
        model.TargetText = "2";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.Kind.Should().Be(PriceAlertKind.FundingCross);
        definition.TargetPrice.Should().Be(2m, "the funding threshold rides the price slot in PERCENT units");
        definition.Trigger.Should().Be(AlertTrigger.Below, "the editor's default edge is the Below option");
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.Funding.Below.Format/2]");
    }

    /// <summary>Zero is an honest funding threshold (the rate can sit exactly on it): the draft lands, never defaulted.</summary>
    [Fact]
    public async Task CreateCommand_AZeroFundingThreshold_IsAnHonestThreshold()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);
        model.SymbolText = "BTC/USDT:USDT";
        model.TargetText = "0";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.TargetPrice.Should().Be(0m, "zero is never substituted for a missing parse");
        model.ErrorMessage.Should().BeNull();
    }

    /// <summary>A negative threshold is honest too (rates go negative); the Above edge rides the cross vocabulary. The integer pins the line culture-invariant.</summary>
    [Fact]
    public async Task CreateCommand_ANegativeFundingThreshold_IsAnHonestThreshold()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);
        model.SelectedTrigger = model.VisibleTriggerOptions.First(option => option.Trigger == AlertTrigger.Above);
        model.SymbolText = "BTC/USDT:USDT";
        model.TargetText = "-2";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.TargetPrice.Should().Be(-2m, "the signed threshold is never defaulted away");
        definition.Trigger.Should().Be(AlertTrigger.Above, "the Above edge composes from the cross vocabulary");
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.Funding.Above.Format/-2]");
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

    #region Indicator Cross Editor (T-203)

    [Fact]
    public void TheIndicatorPicker_ListsOnlyTheLoadedPluginNames()
    {
        var (model, _, _) = BuildWithIndicators(["RSI", "SMA"]);

        model.IndicatorOptions.Should().Equal(["RSI", "SMA"], "the picker is the loaded plugin snapshot, nothing invented");
    }

    [Fact]
    public async Task AnEmptyPluginSet_RefusesTheCrossKind_Outright()
    {
        var (model, engine, _) = Build();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.IndicatorCross);
        model.SymbolText = "BTC/USDT";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.NoIndicatorsLoaded]");
        engine.Roster.Should().BeEmpty("an indicator cross with nothing to resolve is refused before the engine");
    }

    [Fact]
    public void TheCrossKind_HidesTheAtEdge_AndMovesAStaleAtSelectionOntoARealEdge()
    {
        var (model, _, _) = BuildWithIndicators(["RSI"]);
        model.SelectedTrigger = model.TriggerOptions.First(option => option.Trigger == AlertTrigger.At);

        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.IndicatorCross);

        model.VisibleTriggerOptions.Select(option => option.Trigger)
            .Should().Equal([AlertTrigger.Above, AlertTrigger.Below], "the editor never offers the edge it would refuse");
        model.SelectedTrigger.Trigger.Should().Be(AlertTrigger.Below, "a stale At selection moves to a real cross edge");
    }

    [Fact]
    public async Task AValidIndicatorValueCrossDraft_ReachesTheEngine_WithEverySpecFact()
    {
        var (model, engine, _) = BuildWithIndicators(["RSI", "SMA"]);
        using var attach = model.Attach();
        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.IndicatorCross);
        model.SelectedIndicatorName = "RSI";
        model.PeriodText = "14";
        model.LevelText = "70";
        model.SymbolText = "BTC/USDT";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.Kind.Should().Be(PriceAlertKind.IndicatorCross);
        definition.Trigger.Should().Be(AlertTrigger.Below, "the editor's default cross edge is the Below option");
        definition.Indicator.Should().NotBeNull();
        definition.Indicator!.Name.Should().Be("RSI");
        definition.Indicator.Period.Should().Be(14);
        definition.Indicator.Source.Should().Be(AlertCrossSource.IndicatorValue);
        definition.Indicator.Level.Should().Be(70m);
        model.ErrorMessage.Should().BeNull();
        model.Rows[0].ConditionText.Should().Be("[Alert.Condition.Indicator.ValueBelow.Format/RSI|14|70]");
    }

    [Fact]
    public void SelectingACrossRow_LoadsTheStoredSpecIntoTheEditor()
    {
        var (model, _, _) = BuildWithIndicators(["RSI"], CrossFixture());

        model.SelectedRow = model.Rows[0];

        model.SelectedKind.Kind.Should().Be(PriceAlertKind.IndicatorCross);
        model.SelectedTrigger.Trigger.Should().Be(AlertTrigger.Above);
        model.SelectedIndicatorName.Should().Be("RSI");
        model.PeriodText.Should().Be("14", "the editor loads the stored spec's exact facts");
        model.LevelText.Should().Be("70");
        model.SelectedSource.Source.Should().Be(AlertCrossSource.IndicatorValue);
        model.IsIndicatorValueSource.Should().BeTrue("the level field shows for the indicator-value source");
    }

    [Fact]
    public void ThePausedRow_RendersTheHonestPausedStatus()
    {
        var (model, _, _) = BuildWithIndicators(["RSI"], CrossFixture(AlertStatus.Paused));

        model.Rows.Should().ContainSingle();
        model.Rows[0].StatusText.Should().Be("[Alert.Status.Paused]");
    }

    #endregion Indicator Cross Editor (T-203)

    #region Funding Cross Editor (T-240)

    /// <summary>
    /// The funding kind opens the cross vocabulary (Above/Below only, At
    /// never offered — the editor never offers what the engine would
    /// refuse) and keeps the indicator fields hidden.
    /// </summary>
    [Fact]
    public void TheFundingEditor_OpensTheCrossVocabulary_AndNeverOffersAt()
    {
        var (model, _, _) = Build();
        model.IsFundingKind.Should().BeFalse("the default kind is the price level");
        model.IsCrossKind.Should().BeFalse();

        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.FundingCross);

        model.IsFundingKind.Should().BeTrue();
        model.IsCrossKind.Should().BeTrue("a cross reads only the Above or Below edge");
        model.IsIndicatorKind.Should().BeFalse("the indicator fields stay hidden");
        model.IsPriceKind.Should().BeFalse();
        model.VisibleTriggerOptions.Select(option => option.Trigger)
            .Should().Equal([AlertTrigger.Above, AlertTrigger.Below], "At is meaningless for a cross");

        model.SelectedKind = model.KindOptions.First(option => option.Kind == PriceAlertKind.Price);
        model.IsFundingKind.Should().BeFalse();
        model.VisibleTriggerOptions.Select(option => option.Trigger)
            .Should().Contain(AlertTrigger.At, "the price vocabulary keeps its At edge");
    }

    #endregion Funding Cross Editor (T-240)

    #region Re-arm Editor (T-204)

    [Fact]
    public void TheEditor_DefaultsToOneShotReArm_BeepSound_AndAFifteenMinuteCooldownSpelling()
    {
        var (model, _, _) = Build();

        model.SelectedReArm.Mode.Should().Be(AlertReArmMode.OneShot, "the default aftermath is the terminal one-shot");
        model.SelectedSound.Sound.Should().Be(AlertSoundChoice.Beep, "the default sound is the audible beep");
        model.CooldownText.Should().Be("15", "the window spelling preloads the standard window");
        model.IsCooldownMode.Should().BeFalse("the window field shows only for the Cooldown mode");

        model.SelectedReArm = model.ReArmOptions.First(option => option.Mode == AlertReArmMode.Cooldown);
        model.IsCooldownMode.Should().BeTrue("the Cooldown mode shows its window field");
    }

    [Fact]
    public async Task CreateCommand_AValidCooldownSilentDraft_CarriesTheReArmFacts()
    {
        var (model, engine, _) = Build();
        using var attach = model.Attach();
        model.SelectedReArm = model.ReArmOptions.First(option => option.Mode == AlertReArmMode.Cooldown);
        model.CooldownText = "5";
        model.SelectedSound = model.SoundOptions.First(option => option.Sound == AlertSoundChoice.Silent);
        model.SymbolText = "BTC/USDT";
        model.TargetText = "110";

        await model.CreateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var definition = engine.Roster.Should().ContainSingle().Subject;
        definition.ReArm.Should().Be(AlertReArmMode.Cooldown);
        definition.CooldownMinutes.Should().Be(5);
        definition.Sound.Should().Be(AlertSoundChoice.Silent);
        model.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public async Task CreateCommand_AnUnparsableOrNonPositiveCooldownRefusesWithItsOwnLine_AndNeverReachesTheEngine(
        string spelling)
    {
        var (model, engine, _) = Build();
        model.SelectedReArm = model.ReArmOptions.First(option => option.Mode == AlertReArmMode.Cooldown);
        model.CooldownText = spelling;
        model.SymbolText = "BTC/USDT";
        model.TargetText = "110";

        await model.CreateCommand.Execute(RxVoid.Default);

        model.ErrorMessage.Should().Be("[Alert.Error.InvalidCooldown/" + spelling + "]");
        engine.Roster.Should().BeEmpty("a refused window never reaches the engine");
    }

    [Fact]
    public void SelectingACooldownRow_LoadsTheStoredWindowAndSound()
    {
        var (model, _, _) = Build(ReArmFixture());

        model.SelectedRow = model.Rows[0];

        model.SelectedReArm.Mode.Should().Be(AlertReArmMode.Cooldown, "the editor loads the stored aftermath");
        model.CooldownText.Should().Be("5", "the editor loads the stored window's exact fact");
        model.SelectedSound.Sound.Should().Be(AlertSoundChoice.Silent);
        model.IsCooldownMode.Should().BeTrue("the loaded Cooldown mode shows its window field");
    }

    [Fact]
    public async Task UpdateSelectedCommand_CarriesTheReArmFacts()
    {
        var (model, engine, _) = Build(ReArmFixture());
        using var attach = model.Attach();
        model.SelectedRow = model.Rows[0];
        model.SelectedReArm = model.ReArmOptions.First(option => option.Mode == AlertReArmMode.OneShot);
        model.SelectedSound = model.SoundOptions.First(option => option.Sound == AlertSoundChoice.Beep);

        await model.UpdateSelectedCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var reArmed = engine.Roster.Should().ContainSingle().Subject;
        reArmed.Id.Should().Be(ReArmFixture().Id, "an edit keeps the identity");
        reArmed.ReArm.Should().Be(AlertReArmMode.OneShot, "the edit re-arms with the editor's aftermath");
        reArmed.Sound.Should().Be(AlertSoundChoice.Beep);
        reArmed.Status.Should().Be(AlertStatus.Armed);
    }

    #endregion Re-arm Editor (T-204)

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
        model.SelectedReArm.Mode.Should().Be(AlertReArmMode.OneShot, "the editor loads the stored aftermath");
        model.SelectedSound.Sound.Should().Be(AlertSoundChoice.Beep);
        model.CooldownText.Should().Be("0", "the one-shot's unused window loads as its stored fact");
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

    #region The Binding Race (T-225)

    [Fact]
    public void TheNullWrites_NeverUnsetTheStandingOptions()
    {
        var (model, _, _) = Build();

        var kindBefore = model.SelectedKind;
        var triggerBefore = model.SelectedTrigger;
        var sourceBefore = model.SelectedSource;
        var reArmBefore = model.SelectedReArm;
        var soundBefore = model.SelectedSound;
        var indicatorBefore = model.SelectedIndicatorName;

        // The pickers' TwoWay SelectedItem bindings write null while their
        // items are still empty or coercing (the bind/init race): every
        // write is an artifact, the standing option stays.
        model.SelectedKind = null!;
        model.SelectedTrigger = null!;
        model.SelectedSource = null!;
        model.SelectedReArm = null!;
        model.SelectedSound = null!;
        model.SelectedIndicatorName = null!;

        model.SelectedKind.Should().BeSameAs(kindBefore, "a null write is never a user choice");
        model.SelectedTrigger.Should().BeSameAs(triggerBefore, "the standing trigger stays");
        model.SelectedSource.Should().BeSameAs(sourceBefore, "the standing source stays");
        model.SelectedReArm.Should().BeSameAs(reArmBefore, "the standing re-arm stays");
        model.SelectedSound.Should().BeSameAs(soundBefore, "the standing sound stays");
        model.SelectedIndicatorName.Should().Be(indicatorBefore, "the standing indicator name stays");

        // The kind flags answer from the standing facts, never a throw —
        // the get_IsPriceKind dereference was the crash's fatal leg.
        model.IsPriceKind.Should().BeTrue("the standing kind is Price");
        model.IsPercentKind.Should().BeFalse();
        model.IsIndicatorKind.Should().BeFalse();
        model.ShowsTrigger.Should().BeTrue("the Price kind reads the trigger vocabulary");
        model.IsIndicatorValueSource.Should().BeFalse();
        model.IsPriceSource.Should().BeFalse();
        model.IsCooldownMode.Should().BeFalse("the standing re-arm is OneShot");
    }

    [Fact]
    public async Task TheNullWrites_PostOneCoalescedReAssert()
    {
        var (model, _, _) = Build();
        var raises = new List<string>();
        using var sub = model.Changed.Subscribe(args => raises.Add(args.PropertyName ?? string.Empty));

        model.SelectedKind = null!;
        model.SelectedKind = null!;

        raises.Should().BeEmpty("the re-assert rides the deferred post, never the setter");
        await AvaloniaDispatcherPump.DrainAsync();

        raises.Should().ContainSingle().Which.Should().Be(
            nameof(model.SelectedKind),
            "one race's null writes coalesce into one deferred re-assert");
    }

    [Fact]
    public async Task TheSecondRace_PostsItsOwnReAssert()
    {
        var (model, _, _) = Build();
        var raises = new List<string>();
        using var sub = model.Changed.Subscribe(args => raises.Add(args.PropertyName ?? string.Empty));

        model.SelectedKind = null!;
        await AvaloniaDispatcherPump.DrainAsync();
        raises.Should().ContainSingle("the first race's re-assert landed");

        model.SelectedKind = null!;
        raises.Should().ContainSingle("a completed re-assert frees the defense for the next race");
        await AvaloniaDispatcherPump.DrainAsync();
        raises.Should().HaveCount(2);
    }

    #endregion The Binding Race (T-225)
}
