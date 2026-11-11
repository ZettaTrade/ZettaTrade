namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The first-run wizard ViewModel (T-099): the drafts seed from the applied
/// state (never a fabricated default), the step walk is the navigation
/// golden (each step stands alone, the walk clamps at both ends), the finish
/// is the single write path (validate ALL picks first, then apply in the
/// settings-window applier's order — every seam is an existing one), every
/// invalid pick is a refusal with the store untouched (no substituted
/// defaults, no half-applied sets) and a skipped wizard runs no write at
/// all. The symbol-listing facts stay marshal-invariant (the refill lands
/// through ObserveOnUiThread, which a test process does not pump) — the
/// fetch orientation per draft-adapter pick is pinned, the marshalled
/// deliveries are covered by the real dispatcher in the acceptance probe.
/// </summary>
/// <remarks>
/// Rides <see cref="AvaloniaDispatcherSerialCollection"/> (T-138): the
/// wizard's refill posts through <c>ObserveOnUiThread</c> - its callbacks
/// must never execute inside another class's drain window (a poisoned
/// foreign job fails every subsequent drain of the shared dispatcher), so
/// every dispatcher-contact class lives in the one serial collection.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class FirstRunWizardViewModelTests
{
    private readonly IThemeManager _themeManager = Substitute.For<IThemeManager>();
    private readonly IMarketDataAdapter _binance = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _demo = Substitute.For<IMarketDataAdapter>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly FakeLocalization _localization = new();

    public FirstRunWizardViewModelTests()
    {
        _themeManager.CurrentTheme.Returns("dark");
        _themeManager.AvailableThemes.Returns(new[]
        {
            new ThemeInfo("dark", "Dark", true, []),
            new ThemeInfo("light", "Light", true, []),
        });
        _binance.Id.Returns("binance");
        _binance.Name.Returns("Binance");
        _demo.Id.Returns("demo");
        _demo.Name.Returns("Demo");
    }

    #region Test Kit

    private FirstRunWizardViewModel CreateVm() => new(
        _settings,
        _themeManager,
        CreateExchanges(),
        _localization,
        NullLogger<FirstRunWizardViewModel>.Instance);

    /// <summary>The demo lane is the first registered adapter — the shipped registration order — so the facade's default is the demo.</summary>
    private ActiveExchangeService CreateExchanges() => new(
        [_demo, _binance],
        _settings,
        NullLogger<ActiveExchangeService>.Instance);

    #endregion Test Kit

    #region Seeding

    [Fact]
    public void TheDrafts_SeedFromTheAppliedState_NeverAFabricatedDefault()
    {
        var viewModel = CreateVm();

        viewModel.ThemeOptions.Should().HaveCount(2);
        viewModel.SelectedTheme.Should().NotBeNull(
            "the applied theme is already in the available list, so the seed resolves it");
        viewModel.SelectedTheme!.Name.Should().Be("dark");
        viewModel.SelectedAdapter.Should().BeSameAs(
            _demo,
            "the facade's active adapter is the seed, never a fabricated pick");
        viewModel.SelectedSymbolName.Should().BeNull("the symbol pick has no prior state to restore");
        viewModel.SymbolNames.Should().BeEmpty(
            "the listing refresh attaches on the window's activation, never at construction");
    }

    [Fact]
    public void TheDrafts_LeaveTheSeedsNull_WhenTheAppliedStateIsNotResolvable()
    {
        // An applied theme that left the available list (a removed custom
        // theme, say): the seed stays null rather than substituting a default.
        _themeManager.CurrentTheme.Returns("midnight");

        var viewModel = CreateVm();

        viewModel.SelectedTheme.Should().BeNull(
            "an unresolvable applied theme is an honest null, never a substituted default");
    }

    #endregion Seeding

    #region Step Navigation Golden

    [Fact]
    public async Task TheStepWalk_IsTheNavigationGolden_WithClampsAtBothEnds()
    {
        var viewModel = CreateVm();

        // Step one: the theme step, first and last affordances off/on.
        viewModel.SelectedStepIndex.Should().Be(0);
        viewModel.IsThemeStepVisible.Should().BeTrue();
        viewModel.IsAdapterStepVisible.Should().BeFalse();
        viewModel.IsSymbolStepVisible.Should().BeFalse();
        viewModel.IsBackVisible.Should().BeFalse("the walk starts at the front");
        viewModel.IsNextVisible.Should().BeTrue();
        viewModel.IsFinishVisible.Should().BeFalse();

        await viewModel.NextCommand.Execute(RxVoid.Default);

        // Step two: the adapter pick.
        viewModel.SelectedStepIndex.Should().Be(1);
        viewModel.IsThemeStepVisible.Should().BeFalse();
        viewModel.IsAdapterStepVisible.Should().BeTrue();
        viewModel.IsBackVisible.Should().BeTrue();
        viewModel.IsNextVisible.Should().BeTrue();
        viewModel.IsFinishVisible.Should().BeFalse();

        await viewModel.BackCommand.Execute(RxVoid.Default);

        // The back affordance returns to the seeds (the drafts survive it).
        viewModel.SelectedStepIndex.Should().Be(0);
        viewModel.IsThemeStepVisible.Should().BeTrue();

        await viewModel.NextCommand.Execute(RxVoid.Default);
        await viewModel.NextCommand.Execute(RxVoid.Default);

        // Step three: the symbol pick, the finish affordance's own step.
        viewModel.SelectedStepIndex.Should().Be(2);
        viewModel.IsSymbolStepVisible.Should().BeTrue();
        viewModel.IsBackVisible.Should().BeTrue();
        viewModel.IsNextVisible.Should().BeFalse("the walk ends at the last step");
        viewModel.IsFinishVisible.Should().BeTrue();

        await viewModel.NextCommand.Execute(RxVoid.Default);

        viewModel.SelectedStepIndex.Should().Be(
            2,
            "the walk clamps at the last step — a further next never runs past it");

        await viewModel.BackCommand.Execute(RxVoid.Default);
        await viewModel.BackCommand.Execute(RxVoid.Default);
        await viewModel.BackCommand.Execute(RxVoid.Default);

        viewModel.SelectedStepIndex.Should().Be(
            0,
            "the walk clamps at the first step — a further back never runs past it");
        viewModel.IsThemeStepVisible.Should().BeTrue();
    }

    #endregion Step Navigation Golden

    #region The Finish Write Integration

    [Fact]
    public async Task TheFinish_WritesEveryPick_ThroughItsExistingSeam()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);

        viewModel.SelectedTheme = new ThemeInfo("light", "Light", true, []);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "BTC/USDT";

        await viewModel.FinishAsync();

        await _themeManager.Received(1).ApplyThemeAsync(
            "light",
            Arg.Any<CancellationToken>());
        await _settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.Theme,
            "light");
        await _settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId,
            "BTC/USDT");

        // The facade's own write backs the switch (the adapter pick's seam).
        await _settings.Received(1).SetAsync(
            "Workspace",
            "ExchangeId",
            "binance");

        exchanges.Current.Should().BeSameAs(_binance, "the adapter pick activates through the facade");
    }

    [Fact]
    public async Task TheFinish_FollowsTheAppliersOrder_ThemeThenAdapterThenSymbol()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedTheme = new ThemeInfo("light", "Light", true, []);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "BTC/USDT";

        await viewModel.FinishAsync();

        // The settings service's write sequence pins the applier's order: the
        // theme persist before the facade's own exchange persist, the
        // workspace symbol persist always last. The facade's ExchangeId
        // section/key are its own constants — matched by the write's value.
        Received.InOrder(() =>
        {
            // The substitution calls discard their returned tasks on purpose
            // (the recorder only needs the call facts of the sequence; the
            // lambda is a plain Action, so discarding Task returns noiselessly).
            _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                "light");
            _settings.SetAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                "binance");
            _settings.SetAsync(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceSymbolId,
                "BTC/USDT");
        });
    }

    #endregion The Finish Write Integration

    #region The Refusal Lane

    [Fact]
    public async Task TheFinish_WithAnUnpickedTheme_RefusesAndWritesNothing()
    {
        _themeManager.CurrentTheme.Returns("midnight");
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "BTC/USDT";

        var act = async () => await viewModel.FinishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*theme pick is required*");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
        await _themeManager.DidNotReceive().ApplyThemeAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        exchanges.Current.Should().BeSameAs(_demo, "a refused finish never touches the workspace");
    }

    [Fact]
    public async Task TheFinish_WithAnUnregisteredTheme_RefusesAndWritesNothing()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedTheme = new ThemeInfo("midnight", "Midnight", false, []);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "BTC/USDT";

        var act = async () => await viewModel.FinishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a registered theme*");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task TheFinish_WithAnUnpickedAdapter_RefusesAndWritesNothing()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedAdapter = null;
        viewModel.SelectedSymbolName = "BTC/USDT";

        var act = async () => await viewModel.FinishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*adapter pick is required*");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
        exchanges.Current.Should().BeSameAs(_demo, "a refused finish never switches the workspace");
    }

    [Fact]
    public async Task TheFinish_WithAnUnregisteredAdapter_RefusesAndWritesNothing()
    {
        var ghost = Substitute.For<IMarketDataAdapter>();
        ghost.Id.Returns("ghost");
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedAdapter = ghost;
        viewModel.SelectedSymbolName = "BTC/USDT";

        var act = async () => await viewModel.FinishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a registered exchange adapter*");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
        exchanges.Current.Should().BeSameAs(_demo, "a refused finish never switches the workspace");
    }

    [Fact]
    public async Task TheFinish_WithABlankSymbolPick_RefusesAndWritesNothing()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedTheme = new ThemeInfo("light", "Light", true, []);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "   ";

        var act = async () => await viewModel.FinishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*symbol pick is required*");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
        await _themeManager.DidNotReceive().ApplyThemeAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        exchanges.Current.Should().BeSameAs(_demo, "a refused finish never touches the workspace");
    }

    #endregion The Refusal Lane

    #region The Command's Failure Lane

    [Fact]
    public async Task TheFinishCommand_OnAFailingFinish_EmitsFalse_AndKeepsTheStoreUntouched()
    {
        _themeManager.CurrentTheme.Returns("midnight");
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);
        viewModel.SelectedAdapter = _binance;
        viewModel.SelectedSymbolName = "BTC/USDT";

        // The refusal throws inside the finish; the command's catch keeps
        // the execution finished normally, and the false emission is what
        // keeps the window open (the drafts and the skip stay honest).
        var applied = await viewModel.FinishCommand.Execute(RxVoid.Default);

        applied.Should().BeFalse("a refused finish never reports a success to the window");
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    #endregion The Command's Failure Lane

    #region The Skip Path

    [Fact]
    public async Task TheSkipPath_NeverRunsAWrite_AndLeavesTheDemoLaneStanding()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);

        // A full walk with draft switches and a draft pick — everything a
        // user may touch before skipping. Nothing applies mid-walk.
        await viewModel.NextCommand.Execute(RxVoid.Default);
        viewModel.SelectedAdapter = _binance;
        await viewModel.NextCommand.Execute(RxVoid.Default);
        viewModel.SelectedSymbolName = "BTC/USDT";

        // The skip is the plain abandon: no write ever landed, the active
        // adapter is still the demo lane the layout shipped with.
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
        await _themeManager.DidNotReceive().ApplyThemeAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        exchanges.Current.Should().BeSameAs(_demo, "the skipped wizard leaves the demo lane standing");
    }

    #endregion The Skip Path

    #region The Listing Fetch

    [Fact]
    public void EveryDraftAdapterPick_FetchesItsOwnListing_IncludingTheSeed()
    {
        var exchanges = CreateExchanges();
        var viewModel = new FirstRunWizardViewModel(
            _settings,
            _themeManager,
            exchanges,
            _localization,
            NullLogger<FirstRunWizardViewModel>.Instance);

        using (viewModel.AttachSymbolListing())
        {
            viewModel.SelectedAdapter = _binance;
        }

        _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
    }

    #endregion The Listing Fetch

    #region The Step Counter

    [Fact]
    public void TheCounter_ReadsTheWalkPosition_ThroughTheParameterizedRecipe()
    {
        var viewModel = CreateVm();

        viewModel.StepCounter.Should().Be(
            "Wizard.Step.Counter:1,3",
            "the counter speaks the 1-based step and the walk's length, the localizer's own recipe");
    }

    [Fact]
    public async Task TheCounter_MovesWithTheWalk_BothDirections()
    {
        var viewModel = CreateVm();

        await viewModel.NextCommand.Execute(RxVoid.Default);

        viewModel.StepCounter.Should().Be(
            "Wizard.Step.Counter:2,3",
            "the counter rides the same raise chain the visibility facts ride");

        await viewModel.BackCommand.Execute(RxVoid.Default);

        viewModel.StepCounter.Should().Be(
            "Wizard.Step.Counter:1,3",
            "the back walk re-points the counter honestly");
    }

    #endregion The Step Counter

    /// <summary>
    /// The wizard's localizer fake: the parameterized recipe records the key
    /// and its args ("Key:arg1,arg2"), so a counter fact reads the exact
    /// format input.
    /// </summary>
    private sealed class FakeLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key) => "[" + key + "]";

        public string GetString(string key, params object[] args) =>
            key + ":" + string.Join(",", args);

        public IObservable<string> LanguageChanged { get; } = Observable.Never<string>();

        public IObservable<string> GetObservable(string key) => Observable.Never<string>();
    }
}
