namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The notification center's display pipeline goldens (T-097): burst
/// coalescing folds one coalescing window's same-kind events into one
/// digest per kind; Do-Not-Disturb suppresses toasts while the flag is on
/// and folds the active span's missed events into exactly ONE summary on
/// the flip back to off; a preseeded (and a live-flipped) mute silences only
/// its own source. The history ring records everything regardless of the
/// mute/DND gates (a mute silences the popup, never the journal).
/// </summary>
/// <remarks>
/// The facts ride the internal test constructor's sub-second coalescing
/// window and real-time waits, so no timing fact may ever reach a real
/// tolerance; every subscription happens BEFORE the triggering publishes
/// (the standing Rx-test hazard rule of the suite).
/// </remarks>
public sealed class NotificationCenterTests
{
    #region Constants

    /// <summary>The sub-second window every timing fact runs on (the internal test constructor).</summary>
    private static readonly TimeSpan TestWindow = TimeSpan.FromMilliseconds(150);

    #endregion

    #region Tests: burst coalescing

    /// <summary>
    /// One coalescing window's same-kind burst folds into ONE digest whose
    /// count is the burst size and whose per-event fields are the newest
    /// event's facts; two kinds inside one window keep separate digests.
    /// </summary>
    [Fact]
    public async Task AOneWindowBurst_FoldsPerKind_IntoDigestsWithTheNewestFacts()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0, "BTC/USDT", OrderSide.Buy, 0.01m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0.AddSeconds(1), "BTC/USDT", OrderSide.Buy, 0.02m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0.AddSeconds(2), "ETH/USDT", OrderSide.Sell, 0.03m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderCancel, moment0, "BTC/USDT", OrderSide.Buy, 0m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderCancel, moment0.AddSeconds(1), "BTC/USDT", OrderSide.Buy, 0m));

        // Past the window's close the digests are settled (a 500 ms tail
        // over a 150 ms window never races).
        await Task.Delay(500);

        var digests = toasts.Where(notification => notification.IsDigest).ToList();
        toasts.Should().HaveCount(2, "burst coalescing folds same-kind events into ONE toast each per window");

        var fillDigest = digests.Single(notification => notification.Kind == NotificationKind.OrderFill);
        fillDigest.Count.Should().Be(5 - 2, "the fill digest counts exactly the three fill events of the window");
        fillDigest.SymbolName.Should().Be("ETH/USDT", "the per-event facts are the newest event's");
        fillDigest.Quantity.Should().Be(0.03m);
        fillDigest.Side.Should().Be(OrderSide.Sell);

        var cancelDigest = digests.Single(notification => notification.Kind == NotificationKind.OrderCancel);
        cancelDigest.Count.Should().Be(2, "the cancel digest counts the two cancel events");
        cancelDigest.SymbolName.Should().Be("BTC/USDT");
    }

    /// <summary>
    /// Events outside one another's windows stay raw single-event toasts:
    /// coalescing never merges across windows.
    /// </summary>
    [Fact]
    public async Task EventsInSeparateWindows_StayRawToasts()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0, "BTC/USDT", OrderSide.Buy, 0.01m));
        await Task.Delay(500);

        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0.AddMinutes(1), "BTC/USDT", OrderSide.Buy, 0.02m));
        await Task.Delay(500);

        toasts.Should().HaveCount(2, "each window's single event stays one raw toast");
        toasts[0].IsDigest.Should().BeFalse();
        toasts[1].IsDigest.Should().BeFalse();
        toasts[1].Quantity.Should().Be(0.02m, "the second toast carries the second event's own facts");
    }

    #endregion

    #region Tests: Do-Not-Disturb

    /// <summary>
    /// The DND replay golden: while the flag is on every toastable event
    /// skips the popup but keeps its history row; the flip back to off
    /// emits EXACTLY one summary counting the missed events, and the
    /// summary alone — the missed rows never re-toast.
    /// </summary>
    [Fact]
    public async Task DoNotDisturb_HoldsToasts_AndTheFlipBackEmitsExactlyOneSummary()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        // Flip DND on through the reactive settings (a drawer toggle's
        // persisted write lands exactly this way), then let five fills
        // arrive during the active span.
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 15, 0, TimeSpan.Zero);
        for (var index = 0; index < 5; index++)
        {
            center.Publish(Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                moment0.AddSeconds(index),
                "BTC/USDT",
                OrderSide.Buy,
                0.01m));
            await Task.Delay(40);
        }

        toasts.Should().BeEmpty("no toast leaves while Do-Not-Disturb holds");
        center.HistorySnapshot.Should().HaveCount(5, "the missed events keep their history rows");

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, false);

        var summaries = toasts.Where(notification => notification.Kind == NotificationKind.DoNotDisturbSummary).ToList();
        summaries.Should().ContainSingle("the closed span folds into exactly one system summary");
        var summary = summaries[0];
        summary.Count.Should().Be(5, "the summary counts exactly the span's missed (toastable) events");
        summary.Route.Should().Be(NotificationRoute.HistoryDrawer, "the summary opens the notification history");
        toasts.Should().ContainSingle("the missed rows never re-toast — the summary is the only DND emission");
    }

    /// <summary>
    /// The flip to ON (a closing flip that TURNED DND on) emits nothing:
    /// only a span that actually held missed events produces a summary,
    /// and only an off-closing flip produces that.
    /// </summary>
    [Fact]
    public async Task FlippingDndOn_NeverEmitsASummary()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        await Task.Delay(250);

        toasts.Should().BeEmpty("the on-flip opens a span, it never folds one");
    }

    #endregion

    #region Tests: per-source mutes

    /// <summary>
    /// A preseeded mute holds the display lane for its own source only:
    /// muted fills publish nothing, unmuted cancels toast as usual, and
    /// BOTH keep their history rows (a mute silences the popup, never the
    /// journal).
    /// </summary>
    [Fact]
    public async Task APreseededMute_SilencesItsSourceOnly_AndHistoryKeepsRecording()
    {
        var settings = new FakeSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteFill, true);
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0, "BTC/USDT", OrderSide.Buy, 0.01m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderCancel, moment0, "BTC/USDT", OrderSide.Buy, 0m));

        await Task.Delay(500);

        toasts.Should().ContainSingle("only the unmuted source's event toasts");
        toasts[0].Kind.Should().Be(NotificationKind.OrderCancel);
        center.HistorySnapshot.Should().HaveCount(2, "the muted fill still entered the session history");
    }

    /// <summary>
    /// The mute persistence golden's live half: flipping the persisted key
    /// mid-session (the reactive change stream) re-applies without a
    /// restart — a previously muted source's next event toasts again.
    /// </summary>
    [Fact]
    public async Task ALiveMuteFlip_ReAppliesWithoutARestart()
    {
        var settings = new FakeSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteFill, true);
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0, "BTC/USDT", OrderSide.Buy, 0.01m));
        await Task.Delay(500);
        toasts.Should().BeEmpty("the preseeded mute held the source");

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteFill, false);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0.AddMinutes(1), "BTC/USDT", OrderSide.Buy, 0.02m));
        await Task.Delay(500);

        toasts.Should().ContainSingle("the live un-mute released the source");
        toasts[0].Kind.Should().Be(NotificationKind.OrderFill);
    }

    /// <summary>
    /// The mute's DND interaction: a muted source's events never count in
    /// a Do-Not-Disturb summary (they were never toastable at all).
    /// </summary>
    [Fact]
    public async Task MutedEventsDuringDnd_CountInNeitherToastsNorTheSummary()
    {
        var settings = new FakeSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteFill, true);
        var center = CreateCenter(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = center.Toasts.Subscribe(
            toasts.Add,
            ex => throw ex);

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.Zero);
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderFill, moment0, "BTC/USDT", OrderSide.Buy, 0.01m));
        center.Publish(Notification.ForJournalOutcome(NotificationKind.OrderReject, moment0, "BTC/USDT", OrderSide.Buy, 0m, "insufficient margin"));
        await Task.Delay(250);

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, false);

        var summary = toasts.Single(notification => notification.Kind == NotificationKind.DoNotDisturbSummary);
        summary.Count.Should().Be(1, "the muted fill was never toastable — only the rejection was missed");
        toasts.Should().ContainSingle("the summary is the only emission of the span");
    }

    /// <summary>
    /// The muted-kinds mirror: the subscription-time seed reports the
    /// preseeded truth and a live change re-orders the set the very same
    /// subscription reports.
    /// </summary>
    [Fact]
    public async Task TheMutedKindsMirror_SeedsWithThePersistedSet_AndTracksLiveChanges()
    {
        var settings = new FakeSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteFill, true);
        var center = CreateCenter(settings);

        var emitted = new List<IReadOnlySet<NotificationKind>>();
        using var subscription = center.MutedKinds.Subscribe(
            emitted.Add,
            ex => throw ex);

        emitted[0].Should().ContainSingle("the persisted preseed arrives as the seed");
        emitted[0].Should().Contain(NotificationKind.OrderFill);

        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteReject, true);

        emitted[^1].Should().Contain(NotificationKind.OrderFill);
        emitted[^1].Should().Contain(NotificationKind.OrderReject, "the live change re-orders the same subscription's set");
    }

    /// <summary>
    /// The Do-Not-Disturb mirror: the persisted seed lands once and a
    /// foreign-section change re-applies nothing (the mirror stays quiet).
    /// </summary>
    [Fact]
    public async Task TheDndMirror_SeedsWithThePersistedFlag_AndIgnoresForeignChanges()
    {
        var settings = new FakeSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        var center = CreateCenter(settings);

        var emitted = new List<bool>();
        using var subscription = center.DoNotDisturb.Subscribe(
            emitted.Add,
            ex => throw ex);

        // The seed + DistinctUntilChanged pass exactly the persisted truth.
        emitted.Should().ContainSingle();
        emitted[0].Should().BeTrue();

        await settings.SetAsync("Chat", NotificationSettingsKeys.DoNotDisturb, false);

        emitted.Should().ContainSingle("a foreign-section change never re-applies the mirror");
    }

    #endregion

    #region Tests: the click-route matrix

    /// <summary>
    /// The click-route matrix (the task's click-route golden): every kind
    /// whose readable panel exists routes to that panel, the kinds with no
    /// deeper panel route to <see cref="NotificationRoute.None"/> (the
    /// click only dismisses), and an unnamed kind faults instead of
    /// defaulting. The expected answers live HERE — the test owns the
    /// matrix and never reads it back from the map it pins.
    /// </summary>
    [Fact]
    public void TheClickRouteMatrix_ArrivesOnePanelPerKind_AndFaultsOnUnnamed()
    {
        NotificationRouteMap.Map(NotificationKind.OrderFill)
            .Should().Be(NotificationRoute.TradeStatistics, "a fill's outcome reads in the trade statistics window");
        NotificationRouteMap.Map(NotificationKind.OrderCancel)
            .Should().Be(NotificationRoute.OrderHistory, "a cancellation's record reads in the order journal");
        NotificationRouteMap.Map(NotificationKind.OrderReject)
            .Should().Be(NotificationRoute.OrderHistory, "a rejection's record reads in the order journal");
        NotificationRouteMap.Map(NotificationKind.AdapterState)
            .Should().Be(NotificationRoute.None, "the health strip lives in the status bar — no deeper panel exists to open");
        NotificationRouteMap.Map(NotificationKind.DoNotDisturbSummary)
            .Should().Be(NotificationRoute.HistoryDrawer, "the summary's readable surface is the notification history");
        NotificationRouteMap.Map(NotificationKind.AlertFired)
            .Should().Be(NotificationRoute.None, "the alerts manager opens from the Trading menu — a fire's click only dismisses");
        NotificationRouteMap.Map(NotificationKind.UpdateAvailable)
            .Should().Be(NotificationRoute.ReleasePage, "the update announcement's click opens the release page (the toast host shells it out, T-213)");

        var act = () => NotificationRouteMap.Map((NotificationKind)int.MaxValue);
        act.Should().Throw<ArgumentOutOfRangeException>("an unnamed kind faults, never a silently substituted route");
    }

    /// <summary>
    /// A digest rides the route of its shared kind (the mined facts are the
    /// newest event's): a fill digest routes to the statistics window, a
    /// cancel digest to the journal — the matrix holds for coalesced
    /// bursts exactly as for raw events.
    /// </summary>
    [Fact]
    public void ADigest_RoutesThroughItsSharedKind()
    {
        var fillDigest = Notification.ComposeDigest(
        [
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.01m),
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 8, 9, 0, 1, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.02m),
        ]);
        fillDigest.Route.Should().Be(NotificationRoute.TradeStatistics);

        var cancelDigest = Notification.ComposeDigest(
        [
            Notification.ForJournalOutcome(
                NotificationKind.OrderCancel,
                new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0m),
            Notification.ForJournalOutcome(
                NotificationKind.OrderCancel,
                new DateTimeOffset(2026, 11, 8, 9, 0, 1, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0m),
        ]);
        cancelDigest.Route.Should().Be(NotificationRoute.OrderHistory);
    }

    /// <summary>
    /// The update announcement's display row composes the localized
    /// summary from the announcement's own version (the release URL rides
    /// the route; the badge never appears — the announcement is one
    /// event, never a burst).
    /// </summary>
    [Fact]
    public void AnUpdateAnnouncement_ComposesItsSummaryFromTheAnnouncedVersion()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString("Notif.UpdateAvailable", Arg.Any<object[]>())
            .Returns(call => $"[Notif.UpdateAvailable:{call.ArgAt<object[]>(1)[0]}]");

        var row = NotificationRow.ForDisplay(
            Notification.ForUpdateAvailable(
                "0.1.0-alpha.159",
                "https://github.com/zettatrade/zettatrade/releases/tag/v0.1.0-alpha.159",
                new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero)),
            localization);

        row.Summary.Should().Be("[Notif.UpdateAvailable:0.1.0-alpha.159]");
        row.HasCountBadge.Should().BeFalse("the announcement is one event, never a burst");
        row.HasRoute.Should().BeTrue("the release page is the row's route");
    }

    #endregion

    #region Tests: the journal outcome surface

    /// <summary>
    /// PublishTradeOutcome announces only terminal, toast-worthy journal
    /// facts: a zero-filled Filled row drops like the trade statistics
    /// scan drops it, and in-flight rows (a still-pending placement, a
    /// submitted order, a partial before completion) carry nothing.
    /// </summary>
    [Fact]
    public void PublishTradeOutcome_AnnouncesTheTerminalFacts_AndDropsTheZeroFilledRow()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);
        var events = new List<Notification>();
        using var eventsSubscription = center.Events.Subscribe(
            events.Add,
            ex => throw ex);

        center.PublishTradeOutcome(BuildOrder(OrderStatus.Pending, filled: 0m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.Submitted, filled: 0m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.PartiallyFilled, filled: 0.005m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.Filled, filled: 0m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.Filled, filled: 0.01m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.Cancelled, filled: 0m));
        center.PublishTradeOutcome(BuildOrder(OrderStatus.Rejected, filled: 0m, rejectReason: "insufficient balance"));

        events.Should().HaveCount(3, "exactly the Filled-with-quantity, Cancelled and Rejected rows announce; every in-flight row stays quiet");
        events.Select(notification => notification.Kind)
            .Should()
            .Equal(NotificationKind.OrderFill, NotificationKind.OrderCancel, NotificationKind.OrderReject);
        events[0].Quantity.Should().Be(0.01m);
        events[2].Reason.Should().Be("insufficient balance");
        events[2].SymbolName.Should().Be(Symbol.Parse("BTC/USDT").Normalized);
    }

    #endregion

    #region Tests: the history surfaces

    /// <summary>
    /// The snapshot rides newest-first and the ring trims at its cap; a
    /// clear empties the drawer without touching the lanes.
    /// </summary>
    [Fact]
    public void TheHistorySnapshot_IsNewestFirst_TrimsAtTheCap_AndClears()
    {
        var settings = new FakeSettings();
        var center = CreateCenter(settings);

        var moment0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        var total = NotificationCenter.MaxHistoryEntries + 3;
        for (var index = 0; index < total; index++)
        {
            center.Publish(Notification.ForAdapterState(
                "Paper",
                AdapterHealthState.Connected,
                moment0.AddSeconds(index)));
        }

        var snapshot = center.HistorySnapshot;
        snapshot.Should().HaveCount(NotificationCenter.MaxHistoryEntries);
        snapshot[0].OccurredUtc.Should().Be(moment0.AddSeconds(total - 1), "the newest is first");
        snapshot[^1].OccurredUtc.Should().Be(moment0.AddSeconds(3), "the oldest three left the ring");

        center.ClearHistory();
        center.HistorySnapshot.Should().BeEmpty();
    }

    #endregion

    #region Helpers    /// <summary>Create a center pinned to the sub-second test window.</summary>
    private static NotificationCenter CreateCenter(FakeSettings settings) =>
        new(settings, NullLogger<NotificationCenter>.Instance, TestWindow);

    /// <summary>A journaled order row whose terminal facts the outcome surface reads.</summary>
    private static Order BuildOrder(OrderStatus status, decimal filled, string rejectReason = "") => new()
    {
        Id = "paper-1",
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.02m),
        Price = new Price(50_000m),
        FilledQuantity = new Quantity(filled),
        RejectReason = rejectReason.Length == 0 ? null : rejectReason,
        Status = status,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 11, 8, 9, 0, 5, TimeSpan.Zero),
    };

    /// <summary>
    /// An in-memory reactive settings double: the persisted reads answer
    /// the store (typed), the writes land in it AND announce themselves on
    /// the change stream (the reactive settings canon in miniature), so a
    /// test drives the re-application exactly the way a drawer flip does.
    /// </summary>
    private sealed class FakeSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        private readonly Subject<SettingsChange> _changes = new();

        public IObservable<SettingsChange> Changes => _changes;

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            if (_store.TryGetValue($"{section}\u0001{key}", out var value) && value is T typed)
            {
                return Task.FromResult(typed);
            }

            return Task.FromResult(defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            var storageKey = $"{section}\u0001{key}";
            var old = _store.TryGetValue(storageKey, out var previous) ? previous : null;
            _store[storageKey] = value;
            _changes.OnNext(new SettingsChange(section, key, old, value));
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    #endregion
}
