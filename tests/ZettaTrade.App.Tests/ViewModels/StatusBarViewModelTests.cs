namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Tests for the status bar: the message channel must surface a transient
/// event message over the Ready idle text and vanish back (replace
/// semantics, language-safe); the exchange picker is hidden for a single
/// adapter, visible (and switching-driving) for two; the persisted-choice
/// fallback plays the loud localized notice in the connection chip for a
/// bounded window and is not silently overwritten by a regular state
/// refresh before it expires.
/// </summary>
/// <remarks>
/// Runs inside <see cref="AvaloniaDispatcherSerialCollection"/>: the
/// fallback-notice writes ride <c>ObserveOnUiThread</c>, so their only
/// execution path is the process-wide owner thread — the
/// <see cref="AvaloniaDispatcherPump"/> every drain of these facts rides.
/// Drains and assertions of different facts in the collection must stay
/// serialized, which the collection guarantees.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class StatusBarViewModelTests
{
    /// <summary>The notice facts drain through the pump; claim it once, before any fact.</summary>
    static StatusBarViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    private readonly ILocalizationManager _localizationManager = Substitute.For<ILocalizationManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMarketDataAdapter _first = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _second = Substitute.For<IMarketDataAdapter>();
    private readonly ITradingAdapter _traded = Substitute.For<ITradingAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly Subject<IReadOnlyList<AdapterHealth>> _health = new();
    private readonly Subject<Notification> _events = new();

    public StatusBarViewModelTests()
    {
        _localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        // The chip facts' captions resolve in a predictable, test-visible
        // form (the key itself); fact-level setups below pin exact values
        // where they care, and those land AFTER this broad one.
        _localizationManager.GetString(Arg.Any<string>()).Returns(call => $"[{call.ArgAt<string>(0)}]");
        _localizationManager.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => $"[{call.ArgAt<string>(0)}]");

        // The shipped trading surface: the paper simulator parks the chip
        // on the paper lane unless a fact swaps the identity.
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);

        _first.Id.Returns("first");
        _first.Name.Returns("First");
        _second.Id.Returns("second");
        _second.Name.Returns("Second");

        // No persisted choice: the facade restore (and any explicit one in
        // the fallback tests) always starts here.
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
    }

    private ActiveExchangeService CreateService(int adapters = 2)
    {
        var catalog = adapters == 1 ? new[] { _first } : new[] { _first, _second };
        return new ActiveExchangeService(
            catalog,
            _settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private TradingModeService CreateTradingModeService() =>
        new(
            _traded,
            new ExchangeEndpointRegistry(
                _settings,
                NullLogger<ExchangeEndpointRegistry>.Instance),
            _confirmation,
            NullLogger<TradingModeService>.Instance);

    private StatusBarViewModel CreateViewModel(
        ActiveExchangeService? exchanges = null,
        TimeSpan? fallbackNoticeDuration = null,
        TimeSpan? statusMessageDuration = null,
        TradingModeService? tradingMode = null,
        IObservable<UpdateRelease?>? updateReleases = null) =>
        new(
            _localizationManager,
            exchanges ?? CreateService(),
            tradingMode ?? CreateTradingModeService(),
            _events,
            _health,
            fallbackNoticeDuration,
            statusMessageDuration,
            NullLogger<StatusBarViewModel>.Instance,
            updateReleases);

    #region Tests: the message channel (T-153)

    /// <summary>One journal-cancel event the channel can surface (the composed summary renders "[Notif.CancelOn]").</summary>
    private static Notification CancelEvent() =>
        Notification.ForJournalOutcome(
            NotificationKind.OrderCancel,
            DateTimeOffset.UtcNow,
            "BTCUSDT",
            ZettaTrade.Core.Enums.OrderSide.Buy,
            0m);

    /// <summary>One journal-fill event the channel can surface (the composed summary renders "[Notif.Fill]").</summary>
    private static Notification FillEvent() =>
        Notification.ForJournalOutcome(
            NotificationKind.OrderFill,
            DateTimeOffset.UtcNow,
            "ETHUSDT",
            ZettaTrade.Core.Enums.OrderSide.Sell,
            1.5m);

    [Fact]
    public void TheIdleStrip_ShowsTheReadyText()
    {
        var viewModel = CreateViewModel();

        viewModel.StatusMessage.Should().Be("[Status.Ready]", "the idle strip cell is the localized Ready text");
    }

    [Fact]
    public async Task AStatusEvent_SurfacesItsMessageThenVanishesToTheReadyText()
    {
        var viewModel = CreateViewModel(statusMessageDuration: TimeSpan.FromMilliseconds(60));

        // Act: the event arrives. BUG-018 discipline: the message write is
        // POSTED to the UI thread, so BEFORE any drain nothing landed.
        _events.OnNext(CancelEvent());

        viewModel.StatusMessage.Should().Be("[Status.Ready]", "a posted message cannot write the cell before the drain");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be("[Notif.CancelOn]", "the event surfaces its composed summary over the idle text");

        // The expiry is a thread-pool timer; the 800 ms wait (the suite runs
        // under sparse parallel processes) far exceeds the 60 ms window.
        await Task.Delay(800);

        // BUG-018 discipline: the 60 ms expiry timer elapsed above, yet the
        // cell still shows the message — the timer thread wrote nothing; its
        // refresh is posted to the dispatcher like every other write.
        viewModel.StatusMessage.Should().Be(
            "[Notif.CancelOn]",
            "an elapsed expiry timer must not write the cell from its own thread");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be("[Status.Ready]", "the message vanishes to the idle text after its quiet period");
    }

    [Fact]
    public async Task ASecondEvent_ReplacesTheDisplayedMessage()
    {
        var viewModel = CreateViewModel(statusMessageDuration: TimeSpan.FromMilliseconds(60));

        _events.OnNext(CancelEvent());
        _events.OnNext(FillEvent());

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be("[Notif.Fill]", "the newest event's message replaces the displayed one, nothing queues");

        await Task.Delay(800);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be("[Status.Ready]", "one vanishing ride covers the replaced message: the burst coalesces into the displayed latest");
    }

    [Fact]
    public async Task ALanguageSwitch_CannotRevertALiveMessageAndReDerivesTheIdleText()
    {
        _localizationManager.GetString("Status.Ready").Returns("Ready");
        var languageChanged = new BehaviorSubject<string>("en");
        _localizationManager.LanguageChanged.Returns(languageChanged);

        var viewModel = CreateViewModel(statusMessageDuration: TimeSpan.FromMilliseconds(60));

        // Act: an event surfaces its message; a language switch lands while
        // the message holds.
        _events.OnNext(CancelEvent());
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be("[Notif.CancelOn]");

        // The switch re-derives the idle text — but the displayed message
        // keeps its arrival-composed text (the row snapshot discipline).
        _localizationManager.GetString("Status.Ready").Returns("Готов");
        languageChanged.OnNext("ru");
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be(
            "[Notif.CancelOn]",
            "a language switch must not silently revert a live message to the idle text");

        await Task.Delay(800);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.StatusMessage.Should().Be(
            "Готов",
            "after expiry the idle text re-derives from the CURRENT language's catalog");
    }

    #endregion

    [Fact]
    public void ToDisplayVersion_TrimsTheGitSuffixTheSdkBakes()
    {
        AppVersion.ToDisplayVersion("0.1.0-alpha.12+de116cb").Should().Be("0.1.0-alpha.12");
        AppVersion.ToDisplayVersion("0.1.0-alpha.12").Should().Be("0.1.0-alpha.12");
    }

    [Fact]
    public void ToDisplayVersion_WithoutAttributeValue_NeverBlank()
    {
        AppVersion.ToDisplayVersion(null).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UpdateConnectionStatus_ReportsLocalizedStatePerValue()
    {
        _localizationManager.GetString("Status.Connected").Returns("Connected");
        _localizationManager.GetString("Status.Disconnected").Returns("Disconnected");

        var viewModel = CreateViewModel();

        viewModel.UpdateConnectionStatus(true);
        viewModel.ConnectionStatus.Should().Be("Connected", "the connected key is localized");

        viewModel.UpdateConnectionStatus(false);
        viewModel.ConnectionStatus.Should().Be("Disconnected", "the disconnected key is localized");
    }

    [Fact]
    public void LanguageSwitch_KeepsTheConnectionChipLocalized()
    {
        var languageChanged = new BehaviorSubject<string>("en");
        _localizationManager.LanguageChanged.Returns(languageChanged);
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var viewModel = CreateViewModel();
        viewModel.UpdateConnectionStatus(true);

        languageChanged.OnNext("ru");

        viewModel.ConnectionStatus.Should().Be("Connected", "the chip re-derives from the stored state, not from a stale text");
    }

    [Fact]
    public void BeforeTheFirstConnectionReport_TheChipStaysEmpty()
    {
        var viewModel = CreateViewModel();

        viewModel.ConnectionStatus.Should().BeEmpty("no state report means no chip text");
    }

    [Fact]
    public void SingleRegisteredAdapter_ThePickerStaysHidden()
    {
        var service = CreateService(adapters: 1);

        var viewModel = CreateViewModel(service);

        viewModel.HasExchangeChoice.Should().BeFalse("a selector over one entry is meaningless by design");
        viewModel.ExchangeNames.Should().ContainSingle().Which.Should().Be("First");
        viewModel.ActiveExchangeName.Should().Be("First");
        viewModel.SelectedExchangeName.Should().Be("First");
    }

    [Fact]
    public void TwoRegisteredAdapters_ThePickerIsVisibleWithBothNames()
    {
        var viewModel = CreateViewModel();

        viewModel.HasExchangeChoice.Should().BeTrue("two registered entries make the selector meaningful");
        viewModel.ExchangeNames.Should().ContainInOrder(
            new[] { "First", "Second" },
            "registration order feeds the picker");
        viewModel.SelectedExchangeName.Should().Be("First", "the picker mirrors the behavioral facade");
        viewModel.ActiveExchangeName.Should().Be("First");
    }

    [Fact]
    public async Task PickerSelection_DrivesTheSwitchThroughTheFacade()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        var flipped = new TaskCompletionSource<IMarketDataAdapter>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emissions = 0;
        using var changes = service.AdapterChanges.Subscribe(adapter =>
        {
            if (++emissions == 2)
            {
                flipped.TrySetResult(adapter);
            }
        });

        // Act: the user picks the second exchange in the picker.
        viewModel.SelectedExchangeName = "Second";

        // The picker selection must flip the facade promptly.
        var winner = await Task.WhenAny(flipped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(flipped.Task, "the picker selection must flip the facade promptly");
        (await flipped.Task).Should().Be(_second);

        await _settings.Received(1).SetAsync("Workspace", "ExchangeId", "second");
        viewModel.ActiveExchangeName.Should().Be("Second", "the chip follows the switch");
        viewModel.SelectedExchangeName.Should().Be("Second", "the facade echo dedupes, the picker keeps the pick");
    }

    [Fact]
    public async Task UnavailablePersistedChoice_PlaysTheNoticeThenExpiresToTheRegularChip()
    {
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("gamma");
        _localizationManager.GetString("Status.ExchangeFallback").Returns("Saved exchange unavailable");
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var service = CreateService();
        var viewModel = CreateViewModel(service, TimeSpan.FromMilliseconds(60));
        viewModel.UpdateConnectionStatus(true);

        // Act: the restore cannot honor the persisted choice and falls back.
        // BUG-018 discipline: the notice write is POSTED to the UI thread,
        // so BEFORE any drain nothing landed (the pre-fix code wrote the
        // chip inline on the emitting thread).
        await service.InitializeAsync();

        viewModel.ConnectionStatus.Should().Be("Connected", "a posted notice cannot write the chip before the drain");

        await AvaloniaDispatcherPump.DrainAsync();
        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable", "the fallback warning owns the chip once its post executes");

        // The expiry is a thread-pool timer; the 800 ms wait (the suite runs
        // under sparse parallel processes) far exceeds the 60 ms window.
        await Task.Delay(800);

        // BUG-018 discipline: the 60 ms expiry timer elapsed above, yet the
        // chip still shows the notice — the timer thread wrote nothing; its
        // refresh is posted to the dispatcher like every other write.
        viewModel.ConnectionStatus.Should().Be(
            "Saved exchange unavailable",
            "an elapsed expiry timer must not write the chip from its own thread");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.ConnectionStatus.Should().Be("Connected", "the notice expires within the configured window");
    }

    [Fact]
    public async Task FallbackNotice_SurvivesTheStartupStateReport_UntilExpiry()
    {
        // Arrange: the connection poll's first state report usually lands on
        // top of the notice — the loud warning must not be silently replaced.
        // The window is a generous two seconds: the state report the fact
        // drives must land DETERMINISTICALLY inside it (a 60 ms window used
        // to be eaten between the pump drain and the report under the
        // parallel full-suite load).
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("gamma");
        _localizationManager.GetString("Status.ExchangeFallback").Returns("Saved exchange unavailable");
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var service = CreateService();
        var viewModel = CreateViewModel(service, TimeSpan.FromMilliseconds(2000));

        await service.InitializeAsync();

        // BUG-018 discipline: the notice post has not executed — the chip is
        // still in its untouched nothing-report state.
        viewModel.ConnectionStatus.Should().BeEmpty("the posted notice waits for the dispatcher, unmarshaled writes would have landed already");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable");

        // Act: the regular state report arrives during the notice window.
        viewModel.UpdateConnectionStatus(true);

        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable", "the regular refresh cannot overwrite the notice");

        await Task.Delay(2300);

        viewModel.ConnectionStatus.Should().Be(
            "Saved exchange unavailable",
            "the expired timer's refresh is posted to the dispatcher, not executed from the timer thread");

        // The expiry timer rides the thread pool: under the parallel
        // full-suite load its fire can land after the drain above drained
        // an already-empty queue, so the drain repeats bounded until the
        // posted refresh executes (the pinned discipline stays: the
        // refresh must arrive through the dispatcher, never inline).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (viewModel.ConnectionStatus == "Saved exchange unavailable" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            await AvaloniaDispatcherPump.DrainAsync();
        }

        viewModel.ConnectionStatus.Should().Be("Connected", "after expiry the regular chip text re-derives");
    }

    #region Tests: the trading lane chip (T-123)

    /// <summary>
    /// The shipped paper surface parks the chip on the paper lane: the
    /// standing facts arrive within one posted beat (the chip's writes ride
    /// the shared marshal, so a pre-drain read sees the frozen world).
    /// </summary>
    [Fact]
    public async Task TheStandingPaperSurface_RendersThePaperChip()
    {
        var viewModel = CreateViewModel();

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.TradingLaneLabel.Should().Be("[Trading.Lane.Paper]");
        viewModel.TradingLaneTooltip.Should().Be("[Trading.Lane.TooltipPaper]");
        viewModel.IsPaperLane.Should().BeTrue("the paper surface is the only safe lane");
        viewModel.IsTestnetLane.Should().BeFalse();
        viewModel.IsLiveLane.Should().BeFalse();
    }

    /// <summary>
    /// A venue surface whose configured endpoint record carries the testnet
    /// identity renders the testnet chip — the lane is the record's
    /// identity, never a URL guess.
    /// </summary>
    [Fact]
    public async Task ATestnetEndpointIdentity_RendersTheTestnetChip()
    {
        _traded.Id.Returns(ExchangeEndpointRegistry.BybitKey);
        _settings
            .GetAsync<BybitEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BybitEndpoints?>())
            .Returns(new BybitEndpoints("https://api.example/testnet", "wss://stream.example/testnet", true));
        var registry = new ExchangeEndpointRegistry(
            _settings,
            NullLogger<ExchangeEndpointRegistry>.Instance);
        await registry.InitializeAsync(CancellationToken.None);

        var viewModel = CreateViewModel(
            tradingMode: new TradingModeService(
                _traded,
                registry,
                _confirmation,
                NullLogger<TradingModeService>.Instance));

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.TradingLaneLabel.Should().Be("[Trading.Lane.Testnet]");
        viewModel.IsTestnetLane.Should().BeTrue();
        viewModel.IsPaperLane.Should().BeFalse();
        viewModel.IsLiveLane.Should().BeFalse();
    }

    /// <summary>
    /// A venue surface on the shipped production record renders the live
    /// chip: the lane is honest the moment the surface exists, before any
    /// order could ride it.
    /// </summary>
    [Fact]
    public async Task AProductionVenueSurface_RendersTheLiveChip()
    {
        _traded.Id.Returns(ExchangeEndpointRegistry.BinanceKey);

        var viewModel = CreateViewModel(
            tradingMode: new TradingModeService(
                _traded,
                new ExchangeEndpointRegistry(
                    _settings,
                    NullLogger<ExchangeEndpointRegistry>.Instance),
                _confirmation,
                NullLogger<TradingModeService>.Instance));

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.TradingLaneLabel.Should().Be("[Trading.Lane.Live]");
        viewModel.IsLiveLane.Should().BeTrue();
        viewModel.IsPaperLane.Should().BeFalse();
        viewModel.IsTestnetLane.Should().BeFalse();
    }

    /// <summary>A language switch re-composes the chip from the standing lane: the caption and tooltip never go stale.</summary>
    /// <remarks>
    /// The call counts: the constructor posts TWO chip renders (the lane
    /// stream's standing delivery and the language BehaviorSubject's own
    /// replay), and the drain runs both. The language switch itself posts
    /// exactly ONE more render — the re-derivation; the second drain lands
    /// it.
    /// </remarks>
    [Fact]
    public async Task ALanguageSwitch_ReComposesTheLaneChipWithinOnePostedBeat()
    {
        var languageChanged = new BehaviorSubject<string>("en");
        _localizationManager.LanguageChanged.Returns(languageChanged);
        var viewModel = CreateViewModel();

        // The pre-drain silence is the LANE CHIP's: the message channel's
        // ctor owns its own idle-text read (Status.Ready), out of this pin's
        // scope.
        _localizationManager.DidNotReceive().GetString("Trading.Lane.Paper");
        _localizationManager.DidNotReceive().GetString("Trading.Lane.TooltipPaper");
        await AvaloniaDispatcherPump.DrainAsync();
        _localizationManager.Received(2).GetString("Trading.Lane.Paper");
        _localizationManager.Received(2).GetString("Trading.Lane.TooltipPaper");

        languageChanged.OnNext("ru");
        viewModel.TradingLaneLabel.Should().Be("[Trading.Lane.Paper]", "the re-composition is posted — the chip keeps its caption until the drain");

        await AvaloniaDispatcherPump.DrainAsync();
        _localizationManager.Received(3).GetString("Trading.Lane.Paper");
        _localizationManager.Received(3).GetString("Trading.Lane.TooltipPaper");
    }

    #endregion Tests: the trading lane chip (T-123)

    #region Tests: the update chip (T-213)

    /// <summary>The checker's verdict shows the chip; the honest empty verdict clears it (a failed check emits nothing — nothing here flaps).</summary>
    [Fact]
    public async Task TheUpdateChip_ShowsTheNewestRelease_AndClearsOnTheEmptyVerdict()
    {
        var releases = new Subject<UpdateRelease?>();
        var viewModel = CreateViewModel(updateReleases: releases);

        viewModel.HasUpdate.Should().BeFalse("no verdict has landed yet — the chip starts hidden");
        viewModel.UpdateTooltip.Should().BeEmpty();

        releases.OnNext(new UpdateRelease("0.1.0-alpha.159", "https://example.com/159"));
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.HasUpdate.Should().BeTrue("a newer release stands");
        viewModel.UpdateTooltip.Should().Be("[Status.UpdateAvailable.Tooltip]", "the tooltip composes localized with the version");

        releases.OnNext(null);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.HasUpdate.Should().BeFalse("the honest empty verdict clears the standing chip");
        viewModel.UpdateTooltip.Should().BeEmpty();
    }

    /// <summary>A language switch re-composes the tooltip from the standing release — the caption never goes stale.</summary>
    [Fact]
    public async Task ALanguageSwitch_ReDerivesTheUpdateTooltipFromTheStandingRelease()
    {
        var releases = new Subject<UpdateRelease?>();
        var languageChanged = new BehaviorSubject<string>("en");
        _localizationManager.LanguageChanged.Returns(languageChanged);
        var cultureStamp = "en";
        _localizationManager
            .GetString("Status.UpdateAvailable.Tooltip", Arg.Any<object[]>())
            .Returns(call => $"[{cultureStamp}:{call.ArgAt<object[]>(1)[0]}]");
        var viewModel = CreateViewModel(updateReleases: releases);

        releases.OnNext(new UpdateRelease("0.1.0-alpha.159", "https://example.com/159"));
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.UpdateTooltip.Should().Be("[en:0.1.0-alpha.159]");

        cultureStamp = "ru";
        languageChanged.OnNext("ru");
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.UpdateTooltip.Should().Be(
            "[ru:0.1.0-alpha.159]",
            "the language switch re-composes the tooltip from the standing release, never from a stale culture");
    }

    #endregion Tests: the update chip (T-213)
}

