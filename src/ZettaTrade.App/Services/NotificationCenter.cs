namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The notification center (T-097): the one publish surface the flows feed
/// and the read surfaces the UI renders. Publishers call
/// <see cref="Publish"/> (never blocks, never throws on a normal payload —
/// a notification is a local record handed to the stream); consumers
/// subscribe <see cref="Events"/> (the raw lane the notification history
/// records) and <see cref="Toasts"/> (the display lane: coalesced,
/// mute-filtered, Do-Not-Disturb-gated). All time-dependent behavior lives
/// HERE on the publishing threads, not on the UI thread — the toast host
/// only renders what arrives.
/// </summary>
/// <remarks>
/// Burst coalescing: the display lane folds every coalescing window's
/// events, grouped by kind, into one digest per present kind (the
/// acceptance's "bursts of dozens of fills coalesce"; the window is a
/// named constant, overridable through the test constructor). Per-source
/// mute: a muted kind stops the display lane for that source only — the
/// history still records what happened (a mute silences the popup, never
/// the order journal). Do-Not-Disturb: events arriving during an active
/// window skip toasts but KEEP their history row; the flip back to off
/// emits ONE system summary counting exactly the missed (not muted!)
/// toastable events of the active span. Every composed stream seeds from
/// the settings service at subscription time (the reactive settings canon:
/// no cached reads, the change stream re-applies live), so an external
/// settings-file edit re-applies without a restart.
/// </remarks>
public sealed class NotificationCenter
{
    #region Private Fields

    /// <summary>How much history the drawer keeps (a session ring; the oldest event leaves first).</summary>
    internal const int MaxHistoryEntries = 200;

    /// <summary>The coalescing window the toasts lane folds bursts inside.</summary>
    private static readonly TimeSpan DefaultCoalescingWindow = TimeSpan.FromSeconds(2);

    /// <summary>The settings-key → notification-kind map of the per-source mutes.</summary>
    private static readonly ImmutableDictionary<string, NotificationKind> MuteKeys = new Dictionary<string, NotificationKind>
    {
        [NotificationSettingsKeys.MuteFill] = NotificationKind.OrderFill,
        [NotificationSettingsKeys.MuteCancel] = NotificationKind.OrderCancel,
        [NotificationSettingsKeys.MuteReject] = NotificationKind.OrderReject,
        [NotificationSettingsKeys.MuteAdapterState] = NotificationKind.AdapterState,
        [NotificationSettingsKeys.MuteAlerts] = NotificationKind.AlertFired,
    }.ToImmutableDictionary();

    private readonly ISettingsService _settings;
    private readonly ILogger<NotificationCenter> _logger;
    private readonly TimeSpan _coalescingWindow;

    /// <summary>The raw inbound event feed.</summary>
    private readonly Subject<Notification> _inbound = new();

    /// <summary>The serialized view of the feed (publishers may run on any thread); every composed stream reads it.</summary>
    private readonly IObservable<Notification> _syncedInbound;

    /// <summary>
    /// The session history ring (oldest first inside); the snapshot property
    /// replays it newest-first.
    /// </summary>
    private readonly ConcurrentQueue<Notification> _history = new();

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the center over the default coalescing window. The composed
    /// streams run per subscription (the host view models attach when they
    /// resolve, which the composition root orders after the settings service
    /// initialized).
    /// </summary>
    public NotificationCenter(ISettingsService settings, ILogger<NotificationCenter> logger)
        : this(settings, logger, DefaultCoalescingWindow)
    {
    }

    /// <summary>
    /// Create the center over an explicit coalescing window (the test
    /// constructor: the goldens pin burst behavior in sub-second window
    /// sizes without ever reaching a real-time tolerance).
    /// </summary>
    /// <param name="settings">The reactive settings service the mutes and the Do-Not-Disturb flag ride.</param>
    /// <param name="logger">The center's own logger.</param>
    /// <param name="coalescingWindow">The burst folding window the display lane buffers inside.</param>
    internal NotificationCenter(
        ISettingsService settings,
        ILogger<NotificationCenter> logger,
        TimeSpan coalescingWindow)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _coalescingWindow = coalescingWindow switch
        {
            { Ticks: > 0 } => coalescingWindow,
            _ => throw new ArgumentOutOfRangeException(nameof(coalescingWindow), coalescingWindow, "The coalescing window must be positive."),
        };
        _syncedInbound = _inbound.Synchronize();
    }

    #endregion Constructor

    #region Publish Surface

    /// <summary>
    /// Publish one notification: it lands in the history ring and rides
    /// both lanes (the display pipeline decides what is toastable). The
    /// Do-Not-Disturb summary rides the display lane only — its facts are
    /// the history rows the drawer already carries, so it never re-rows.
    /// </summary>
    public void Publish(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        AppendHistory(notification);
        _inbound.OnNext(notification);
    }

    /// <summary>
    /// Publish one accepted order's terminal outcome from its journal row.
    /// In-flight rows (placed, submitted, partially filled) carry no
    /// toast-worthy fact here: the fill waits for the Filled row, the
    /// working state is the order's own panel concern. A zero-filled
    /// Filled row is dropped the same way the trade statistics scan drops
    /// it (no trade happened — nothing to announce).
    /// </summary>
    public void PublishTradeOutcome(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        switch (order.Status)
        {
            case OrderStatus.Filled when order.FilledQuantity.Value > 0m:
                Publish(Notification.ForJournalOutcome(
                    NotificationKind.OrderFill,
                    order.UpdatedAt ?? order.CreatedAt,
                    order.Symbol.Normalized,
                    order.Side,
                    order.FilledQuantity.Value));
                break;

            case OrderStatus.Cancelled:
                Publish(Notification.ForJournalOutcome(
                    NotificationKind.OrderCancel,
                    order.UpdatedAt ?? order.CreatedAt,
                    order.Symbol.Normalized,
                    order.Side,
                    order.FilledQuantity.Value));
                break;

            case OrderStatus.Rejected:
                Publish(Notification.ForJournalOutcome(
                    NotificationKind.OrderReject,
                    order.UpdatedAt ?? order.CreatedAt,
                    order.Symbol.Normalized,
                    order.Side,
                    order.FilledQuantity.Value,
                    order.RejectReason ?? string.Empty));
                break;
        }
    }

    #endregion Publish Surface

    #region Read Surface

    /// <summary>
    /// The raw lane: every published event in publish order (serialized
    /// against concurrent publishers). The notification history window
    /// records it; the toasts pipeline reads the same feed internally.
    /// </summary>
    public IObservable<Notification> Events => _syncedInbound;

    /// <summary>
    /// The display lane: digests of the non-muted, non-DND-suppressed
    /// events plus the Do-Not-Disturb summaries the window closes emit.
    /// Each subscription composes its own pipeline (the host view model is
    /// its one subscriber in production); the mute and flag seeds live
    /// ONCE per subscription, shared by the pipeline's gates.
    /// </summary>
    public IObservable<Notification> Toasts => Observable.Create<Notification>(observer =>
    {
        // The seeds live ONCE per display subscription and are shared by
        // every gate inside the pipeline: one reactive settings read per
        // subscription (the seeds complete from the in-memory store, so
        // the share is instant), and every later change re-applies live.
        var mutedShared = ComposeMutedKinds().Replay(1);
        var dndShared = ComposeDoNotDisturb().DistinctUntilChanged().Replay(1);
        var pipeline = ComposeToastPipeline(dndShared, mutedShared).Subscribe(observer);
        var shared = new CompositeDisposable(mutedShared.Connect(), dndShared.Connect());

        return new CompositeDisposable(pipeline, shared);
    });

    /// <summary>
    /// The Do-Not-Disturb flag: the settings snapshot at subscription time,
    /// then every relevant change re-applies it. The history window's
    /// checkbox mirrors it (read-only); an unreadable payload reports
    /// nothing (the flag holds its state, never substitutes a default).
    /// </summary>
    public IObservable<bool> DoNotDisturb => ComposeDoNotDisturb();

    /// <summary>
    /// The muted kinds: the settings snapshot at subscription time, then
    /// every relevant setting re-ordering the set. Only the per-source
    /// mutes participate; the system voice (the Do-Not-Disturb summary)
    /// never mutes.
    /// </summary>
    public IObservable<IReadOnlySet<NotificationKind>> MutedKinds => ComposeMutedKinds();

    /// <summary>The history snapshot, newest first (the drawer's boot page).</summary>
    public IReadOnlyList<Notification> HistorySnapshot
    {
        get
        {
            var copy = _history.ToArray();
            var newestFirst = new Notification[copy.Length];
            for (var i = 0; i < copy.Length; i++)
            {
                newestFirst[copy.Length - 1 - i] = copy[i];
            }

            return newestFirst;
        }
    }

    /// <summary>Clear the history (the history window's Clear command).</summary>
    public void ClearHistory() => _history.Clear();

    #endregion Read Surface

    #region Pipeline

    /// <summary>
    /// The display pipeline. The mute set filters first (a muted source's
    /// events are never toastable, so they count in neither the toasts nor
    /// the Do-Not-Disturb summary). The gate then reads the Do-Not-Disturb
    /// flag AT EVENT TIME: each accepted event is either a toast candidate
    /// (DND off) or a missed event (DND on) — exactly once, classified by
    /// its arrival moment. The candidates buffer into the coalescing
    /// window and fold per kind into digests; the missed events fold, on
    /// each flip back to off, into EXACTLY one summary per active span.
    /// </summary>
    /// <param name="dndFlips">
    /// The shared Do-Not-Disturb flips (the classification, the window
    /// delimiter and the summary pairing read ONE subscription of it).
    /// </param>
    /// <param name="mutedKinds">The shared per-source mute set.</param>
    private IObservable<Notification> ComposeToastPipeline(
        IObservable<bool> dndFlips,
        IObservable<IReadOnlySet<NotificationKind>> mutedKinds)
    {
        return _syncedInbound
            .Publish(events => events
                .WithLatestFrom(mutedKinds, (notification, muted) => (Notification: notification, Muted: muted))
                .Where(entry => !entry.Muted.Contains(entry.Notification.Kind))
                .Select(entry => entry.Notification)
                .Publish(accepted => accepted
                    .WithLatestFrom(dndFlips, (notification, dndActive) => (Notification: notification, DndActive: dndActive))
                    .Publish(gated =>
                    {
                        var toasts = gated
                            .Where(entry => !entry.DndActive)
                            .Select(entry => entry.Notification)
                            .Buffer(_coalescingWindow)
                            .SelectMany(batch => batch
                                .GroupBy(notification => notification.Kind)
                                .Select(group => Notification.ComposeDigest(group.ToList())));

                        var replay = gated
                            .Where(entry => entry.DndActive)
                            .Select(entry => entry.Notification)
                            // Window k spans [flip_{k-1}, flip_k): the missed
                            // events of one ACTIVE span form one window, and
                            // its own closing flip is the boundary that ends
                            // it (the pre-seed head forms window 0 and stays
                            // empty — the events before the seed never
                            // classify and never enter the gate). The pages
                            // materialize while their window is OPEN (hot
                            // windows replay nothing to a late subscriber),
                            // re-emit here the moment the window closes, and
                            // the zip pairs each page with its own closing
                            // flip. A closing flip that turned DND OFF
                            // (flips alternate) is the signature of a window
                            // that covered an ACTIVE span — exactly one
                            // summary per missed span, emitted the moment
                            // the closing flip lands.
                            .Window(dndFlips)
                            .Select(span => span.ToList())
                            .Merge()
                            .Zip(dndFlips, (page, closingDndActive) => (Page: page, ClosingDndActive: closingDndActive))
                            .SelectMany(pair => pair.ClosingDndActive
                                ? Observable.Empty<Notification>()
                                : pair.Page.Count == 0
                                    ? Observable.Empty<Notification>()
                                    : Observable.Return(Notification.ForDoNotDisturbSummary(
                                        pair.Page.Count,
                                        pair.Page[pair.Page.Count - 1].OccurredUtc)));

                        return toasts.Merge(replay);
                    })));
    }

    /// <summary>
    /// Compose the muted kinds: the settings snapshot at subscription time,
    /// then every relevant settings change re-ordering the set. An
    /// unreadable payload mutates nothing (the set holds its state, never
    /// substitutes a default).
    /// </summary>
    private IObservable<IReadOnlySet<NotificationKind>> ComposeMutedKinds()
    {
        var mutations = _settings.Changes
            .Where(change => change.Section == NotificationSettingsKeys.Section &&
                             MuteKeys.ContainsKey(change.Key))
            .Select(change => (Kind: MuteKeys[change.Key], Muted: InterpretBool(change.NewValue)));

        return Observable
            .FromAsync(ReadMutedSnapshotAsync)
            .SelectMany(seed => Observable
                .Return<IReadOnlySet<NotificationKind>>(seed)
                .Concat(mutations
                    .Scan(seed, (current, change) => MutateMutedSet(current, change.Kind, change.Muted))
                    .Select(set => (IReadOnlySet<NotificationKind>)set)));
    }

    /// <summary>
    /// Compose the Do-Not-Disturb flag: the settings snapshot at
    /// subscription time, then every relevant change re-applies it.
    /// </summary>
    private IObservable<bool> ComposeDoNotDisturb() => Observable
        .FromAsync(() => _settings.GetAsync<bool>(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.DoNotDisturb,
            NotificationSettingsKeys.DefaultDoNotDisturb))
        .Concat(_settings.Changes
            .Where(change => change.Section == NotificationSettingsKeys.Section &&
                             change.Key == NotificationSettingsKeys.DoNotDisturb)
            .Select(change => InterpretBool(change.NewValue))
            .Where(flag => flag.HasValue)
            .Select(flag => flag!.Value));

    /// <summary>Read the muted kinds exactly as the settings store holds them right now.</summary>
    private async Task<ImmutableHashSet<NotificationKind>> ReadMutedSnapshotAsync()
    {
        var builder = ImmutableHashSet.CreateBuilder<NotificationKind>();
        foreach (var (key, kind) in MuteKeys)
        {
            var muted = await _settings.GetAsync<bool>(
                NotificationSettingsKeys.Section,
                key,
                NotificationSettingsKeys.DefaultMuted);
            if (muted)
            {
                builder.Add(kind);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Add or remove one kind per its toggle's new truth; an unreadable payload mutates nothing.</summary>
    private static ImmutableHashSet<NotificationKind> MutateMutedSet(
        ImmutableHashSet<NotificationKind> current,
        NotificationKind kind,
        bool? muted) => muted switch
        {
            null => current,
            true when current.Contains(kind) => current,
            true => current.Add(kind),
            false when !current.Contains(kind) => current,
            false => current.Remove(kind),
        };

    /// <summary>
    /// Interpret a settings payload as bool: a bool payload directly, the
    /// hot-reload's JSON element through its value kind, anything else
    /// carries no truth (null).
    /// </summary>
    private static bool? InterpretBool(object? value) => value switch
    {
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    #endregion Pipeline

    #region History

    /// <summary>Append one event to the bounded ring, trimming the oldest past the cap.</summary>
    private void AppendHistory(Notification notification)
    {
        _history.Enqueue(notification);
        while (_history.Count > MaxHistoryEntries && _history.TryDequeue(out _))
        {
            // The trim serves ring maintenance alone: two racing publishers
            // re-read the count each round (the cap is a soft bound — a
            // burst window may overshoot by one entry for one round, and a
            // bounded overshoot never leaks unbounded history).
        }
    }

    #endregion History
}
