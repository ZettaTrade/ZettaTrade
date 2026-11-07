namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The adapter health strip's status bar contract (T-098): the fold's
/// snapshots re-render the dot rows through the shared marshal (a flip
/// never lands from a foreign thread, so every pre-drain read sees a
/// frozen world), the rows carry the composed localized tooltips and a
/// language switch re-composes them from the stored snapshot.
/// </summary>
/// <remarks>
/// Runs inside <see cref="AvaloniaDispatcherSerialCollection"/>: the strip
/// writes ride <c>ObserveOnUiThread</c>, so their only execution path is
/// the process-wide owner thread drained by the
/// <see cref="AvaloniaDispatcherPump"/>. The tooltip composition facts run
/// the internal composer directly (no dispatcher involvement); the
/// localization double is a resx-shaped recording stub because the
/// composer needs exact format resolution, which NSubstitute's expanded
/// <c>params</c> matching would blur.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class StatusBarAdapterHealthTests
{
    /// <summary>The strip facts drain through the pump; claim it once, before any fact.</summary>
    static StatusBarAdapterHealthTests() => AvaloniaDispatcherPump.EnsureInstalled();

    #region Test Data

    /// <summary>An example stamp the tooltip facts pin as text (rendered local-clock by the shared expression).</summary>
    private static readonly DateTimeOffset ExampleMoment = new(2026, 11, 7, 12, 34, 56, TimeSpan.Zero);

    private readonly IThemeManager _themeManager = Substitute.For<IThemeManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMarketDataAdapter _first = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _second = Substitute.For<IMarketDataAdapter>();
    private readonly RecordingLocalization _localization = new();
    private readonly Subject<IReadOnlyList<AdapterHealth>> _health = new();

    public StatusBarAdapterHealthTests()
    {
        _themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        _first.Id.Returns("one");
        _first.Name.Returns("First");
        _second.Id.Returns("two");
        _second.Name.Returns("Second");
    }

    /// <summary>The localized labels/templates the strip facts compose with — the resx shapes, per language.</summary>
    private static readonly Dictionary<string, string> EnStrings = new()
    {
        ["Status.Connected"] = "Connected",
        ["Status.Disconnected"] = "Disconnected",
        ["Status.Health.Reconnecting"] = "Reconnecting",
        ["Status.Health.Unsupervised"] = "No transport supervision",
        ["Status.Health.Tooltip"] = "{0} — {1}. Last event {2}, consecutive failures: {3}.",
        ["Status.Health.TooltipUnsupervised"] = "{0} — {1}.",
    };

    private static readonly Dictionary<string, string> RuStrings = new()
    {
        ["Status.Connected"] = "Подключено",
        ["Status.Disconnected"] = "Отключено",
        ["Status.Health.Reconnecting"] = "Переподключение",
        ["Status.Health.Unsupervised"] = "Транспорт не отслеживается",
        ["Status.Health.Tooltip"] = "{0} — {1}. Последнее событие {2}, подряд проваленных попыток: {3}.",
        ["Status.Health.TooltipUnsupervised"] = "{0} — {1}.",
    };

    #endregion

    #region Tests: rows render within a beat

    /// <summary>
    /// The strip rows render within one posted beat: a fresh view model
    /// starts with no rows, the FIRST snapshot changes nothing before a
    /// drain (the marshal posts — a foreign-thread write would have landed
    /// already), the drain re-renders both rows with their exact composed
    /// tooltips, and a state flip freezes until the next drain again.
    /// </summary>
    [Fact]
    public async Task HealthSnapshots_ReRenderTheRows_WithinOnePostedBeat()
    {
        var viewModel = CreateViewModel();

        viewModel.AdapterHealthRows.Should().BeEmpty("the fresh strip parks before the first snapshot");

        // The first snapshot: one supervised member connected, one
        // unsupervised (the demo's canned-data lane).
        var first = new[]
        {
            Health("one", "First", AdapterHealthState.Connected, ExampleMoment, 0),
            Health("two", "Second", AdapterHealthState.Unsupervised),
        };

        _health.OnNext(first);

        viewModel.AdapterHealthRows.Should().BeEmpty(
            "the snapshot write is posted to the dispatcher — an unmarshaled write would have landed already");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.AdapterHealthRows.Should().HaveCount(2);
        var connected = viewModel.AdapterHealthRows[0];
        connected.AdapterName.Should().Be("First");
        connected.IsConnected.Should().BeTrue();
        connected.IsReconnecting.Should().BeFalse();
        connected.IsDown.Should().BeFalse();
        connected.IsUnsupervised.Should().BeFalse();
        connected.Tooltip.Should().Be($"First — Connected. Last event {ClockText(ExampleMoment)}, consecutive failures: 0.");

        var unsupervised = viewModel.AdapterHealthRows[1];
        unsupervised.AdapterName.Should().Be("Second");
        unsupervised.IsUnsupervised.Should().BeTrue();
        unsupervised.Tooltip.Should().Be("Second — No transport supervision.");

        // The flip: the same member starts reconnecting with a fresh
        // failure counter — the frozen world until the drain again.
        var flipped = new[]
        {
            Health("one", "First", AdapterHealthState.Reconnecting, ExampleMoment, 2),
            Health("two", "Second", AdapterHealthState.Unsupervised),
        };

        _health.OnNext(flipped);

        viewModel.AdapterHealthRows.Should().HaveCount(2, "the flip waits for the drain, the pre-flip rows stay");
        viewModel.AdapterHealthRows[0].IsConnected.Should().BeTrue("the pre-drain rows are the frozen previous snapshot");

        await AvaloniaDispatcherPump.DrainAsync();

        var reconnecting = viewModel.AdapterHealthRows[0];
        reconnecting.IsReconnecting.Should().BeTrue();
        reconnecting.IsConnected.Should().BeFalse();
        reconnecting.Tooltip.Should().Be($"First — Reconnecting. Last event {ClockText(ExampleMoment)}, consecutive failures: 2.");
    }

    /// <summary>
    /// A language switch re-composes the stored snapshot's tooltips on the
    /// drain — the rows survive the switch, only their localized texts
    /// change; a posted re-composition cannot land before the drain.
    /// </summary>
    [Fact]
    public async Task LanguageSwitch_ReComposesTheTooltips_WithinOnePostedBeat()
    {
        var viewModel = CreateViewModel();
        _health.OnNext(
        [
            Health("one", "First", AdapterHealthState.Reconnecting, ExampleMoment, 2),
            Health("two", "Second", AdapterHealthState.Unsupervised),
        ]);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.AdapterHealthRows[0].Tooltip.Should().Be($"First — Reconnecting. Last event {ClockText(ExampleMoment)}, consecutive failures: 2.");

        await _localization.SetLanguageAsync("ru");

        viewModel.AdapterHealthRows[0].Tooltip.Should().Be(
            $"First — Reconnecting. Last event {ClockText(ExampleMoment)}, consecutive failures: 2.",
            "the re-composition is posted — the rows keep the pre-switch texts until the drain");
        viewModel.AdapterHealthRows[1].Tooltip.Should().Be("Second — No transport supervision.");

        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.AdapterHealthRows[0].Tooltip.Should().Be(
            $"First — Переподключение. Последнее событие {ClockText(ExampleMoment)}, подряд проваленных попыток: 2.",
            "the failing counter re-renders, not just the state label");
        viewModel.AdapterHealthRows[1].Tooltip.Should().Be("Second — Транспорт не отслеживается.");
    }

    #endregion

    #region Tests: tooltip composition

    /// <summary>
    /// The supervised tooltip resolves the four-slot template: display
    /// name, the localized state label (the connection chip's shared key),
    /// the compact local clock and the consecutive-failure counter.
    /// </summary>
    [Fact]
    public void SupervisedTooltip_ComposesTheFourSlotTemplate_WithTheSharedStateKey()
    {
        var health = Health("binance", "Binance", AdapterHealthState.Connected, ExampleMoment, 2);

        var tooltip = StatusBarViewModel.ComposeHealthTooltip(_localization, health);

        tooltip.Should().Be($"Binance — Connected. Last event {ClockText(ExampleMoment)}, consecutive failures: 2.");
        _localization.RequestedKeys.Should().Contain("Status.Connected", "the connected label reuses the connection chip's key");
        _localization.RequestedKeys.Should().NotContain("Status.Health.Tooltip", "the four-slot template rides the formatted variant");
        _localization.FormattedKeys.Should().Contain("Status.Health.Tooltip");
    }

    /// <summary>
    /// The unsupervised tooltip resolves the two-slot template only: an
    /// adapter without supervised transport has no event moment and no
    /// counter to compose.
    /// </summary>
    [Fact]
    public void UnsupervisedTooltip_ComposesTheTwoSlotTemplate_WithTheDedicatedStateKey()
    {
        var health = Health("demo", "Demo", AdapterHealthState.Unsupervised);

        var tooltip = StatusBarViewModel.ComposeHealthTooltip(_localization, health);

        tooltip.Should().Be("Demo — No transport supervision.");
        _localization.RequestedKeys.Should().Contain("Status.Health.Unsupervised");
        _localization.RequestedKeys.Should().NotContain(
            "Status.Health.Tooltip",
            "the two-slot prose never asks the four-slot template");
        _localization.FormattedKeys.Should().Contain("Status.Health.TooltipUnsupervised");
        _localization.FormattedKeys.Should().NotContain("Status.Health.Tooltip");
    }

    /// <summary>
    /// The reconnecting label resolves its dedicated key (no connection
    /// chip key exists for the in-cycle phase).
    /// </summary>
    [Fact]
    public void ReconnectingTooltip_UsesTheDedicatedStateKey()
    {
        var health = Health("bybit", "Bybit", AdapterHealthState.Reconnecting, ExampleMoment, 3);

        var tooltip = StatusBarViewModel.ComposeHealthTooltip(_localization, health);

        tooltip.Should().Be($"Bybit — Reconnecting. Last event {ClockText(ExampleMoment)}, consecutive failures: 3.");
        _localization.RequestedKeys.Should().Contain("Status.Health.Reconnecting");
        _localization.RequestedKeys.Should().NotContain("Status.Connected");
    }

    /// <summary>An out-of-vocabulary strip state is a composition error — never a silently substituted label.</summary>
    [Fact]
    public void AnOutOfVocabularyState_TheCompositionErrors()
    {
        var health = Health("x", "X", (AdapterHealthState)99);

        var compose = () => StatusBarViewModel.ComposeHealthTooltip(_localization, health);

        compose.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Helpers

    /// <summary>Render a row's last-event member exactly as the composer does (the tape's pinned precedent).</summary>
    private static string ClockText(DateTimeOffset moment) =>
        moment.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>One fold-shaped health row the facts push through the strip.</summary>
    private static AdapterHealth Health(
        string adapterId,
        string adapterName,
        AdapterHealthState state,
        DateTimeOffset? lastEventUtc = null,
        int consecutiveFailures = 0) =>
        new(adapterId, adapterName, state, lastEventUtc, consecutiveFailures);

    private StatusBarViewModel CreateViewModel() =>
        new(
            _themeManager,
            _localization,
            new ActiveExchangeService(
                [_first, _second],
                _settings,
                NullLogger<ActiveExchangeService>.Instance),
            "0.1.0-test",
            _health,
            logger: NullLogger<StatusBarViewModel>.Instance);

    /// <summary>
    /// The resx-shaped localization double: canned per-language catalogs,
    /// every lookup recorded. Nothing threads through the real resx — the
    /// composer's contract is exact format resolution.
    /// </summary>
    private sealed class RecordingLocalization : ILocalizationManager
    {
        private readonly BehaviorSubject<string> _languages = new("en");

        /// <summary>The plain-lookup keys the composer requested (the state labels).</summary>
        public List<string> RequestedKeys { get; } = [];

        /// <summary>The formatted-lookup keys the composer requested (the tooltip templates).</summary>
        public List<string> FormattedKeys { get; } = [];

        public string CurrentLanguage { get; private set; } = "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } =
            [new("en", "English", "English", true), new("ru", "Russian", "Русский", false)];

        public IObservable<string> LanguageChanged => _languages;

        public string GetString(string key)
        {
            RequestedKeys.Add(key);
            return CurrentLanguage == "ru"
                ? RuStrings.GetValueOrDefault(key, $"<{key}>")
                : EnStrings.GetValueOrDefault(key, $"<{key}>");
        }

        public string GetString(string key, params object[] args)
        {
            // The formatted lookups record ONLY here: the plain-request
            // log stays the state-label log, so the facts can pin which
            // channel each template rode.
            FormattedKeys.Add(key);
            var template = CurrentLanguage == "ru"
                ? RuStrings.GetValueOrDefault(key, $"<{key}>")
                : EnStrings.GetValueOrDefault(key, $"<{key}>");

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        public IObservable<string> GetObservable(string key) => _languages.Select(_ => GetString(key)).StartWith(GetString(key));

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default)
        {
            CurrentLanguage = cultureCode;
            _languages.OnNext(cultureCode);
            return Task.CompletedTask;
        }
    }

    #endregion
}
