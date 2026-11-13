namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Enums;

/// <summary>
/// The notification vocabulary (T-097): one record per notification event;
/// the kinds mirror the sources the center actually serves: the journal's
/// terminal order outcomes (fills, cancellations, rejections), the
/// adapter health flips riding the T-098 fold, and the price-alert fires
/// bridged by the T-204 lane. A Do-Not-Disturb window close folds its
/// missed events into one system summary toast — the summary is a
/// notification of its own kind, while the missed events themselves stay
/// the history's honest rows.
/// </summary>
public enum NotificationKind
{
    /// <summary>A fill (the journal row's Filled outcome, or a burst of them as a digest).</summary>
    OrderFill,

    /// <summary>An accepted cancellation (or a burst of them as a digest).</summary>
    OrderCancel,

    /// <summary>A refused order: the venue rejected the placement, or the journal recorded a Rejected row.</summary>
    OrderReject,

    /// <summary>An adapter transport flip from the T-098 fold (reconnecting, down, recovered).</summary>
    AdapterState,

    /// <summary>The system summary a Do-Not-Disturb window close emits: N missed events are in history.</summary>
    DoNotDisturbSummary,

    /// <summary>A price alert reached its condition (the T-204 bridge from the alert engine's fire path).</summary>
    AlertFired,
}

/// <summary>
/// Where a notification's click routes (the T-012 focus-routing precedent:
/// the ViewModel poses the route, the View owns the window plumbing).
/// <see cref="NotificationRouteMap.Map"/> is the one tested answer per kind —
/// including the kinds that route to <see cref="None"/> (such a click only
/// dismisses the toast).
/// </summary>
public enum NotificationRoute
{
    /// <summary>No route: the click only dismisses the toast. Adapter-state toasts route here — the health strip lives in the always-visible status bar, so no deeper panel exists to open for it.</summary>
    None,

    /// <summary>The trade statistics window: the fills-driven dashboard.</summary>
    TradeStatistics,

    /// <summary>The order history window: the journal of record for cancellations and rejections.</summary>
    OrderHistory,

    /// <summary>The notification history window: a system summary has no deeper panel.</summary>
    HistoryDrawer,
}

/// <summary>
/// The click-route map: the notification kind's target panel, one tested
/// answer per enum state (T-097's click-route matrix). Trade kinds route to
/// the stats/journal windows those outcomes are readable in; adapter states
/// keep <see cref="NotificationRoute.None"/> (the health strip is always
/// visible, so a click only dismisses); the Do-Not-Disturb summary opens
/// the notification history window. A value the map does not name is an
/// argument fault, never a silently substituted route (the trading-safety
/// canon).
/// </summary>
public static class NotificationRouteMap
{
    /// <summary>Route one notification kind to its target panel.</summary>
    /// <param name="kind">The notification kind to route.</param>
    /// <returns>The click target.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A kind the map does not name.</exception>
    public static NotificationRoute Map(NotificationKind kind) => kind switch
    {
        NotificationKind.OrderFill => NotificationRoute.TradeStatistics,
        NotificationKind.OrderCancel => NotificationRoute.OrderHistory,
        NotificationKind.OrderReject => NotificationRoute.OrderHistory,
        NotificationKind.AdapterState => NotificationRoute.None,
        NotificationKind.DoNotDisturbSummary => NotificationRoute.HistoryDrawer,
        NotificationKind.AlertFired => NotificationRoute.None,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "The notification kind has no click-route mapping."),
    };
}

/// <summary>
/// The sink a display row's open command hands its route to: the toast host
/// implements it (dismiss + pose the route through the window's interaction,
/// the T-012 window-plumbing precedent). Rows the host creates carry the
/// command; history rows render read-only and never need it.
/// </summary>
public interface INotificationRouteSink
{
    /// <summary>Activate one notification row: dismiss the toast and land its route (no route: the dismissal is the whole action).</summary>
    /// <param name="row">The row the click came from.</param>
    /// <param name="cancellationToken">Cooperative cancellation of the route landing.</param>
    /// <returns>A task completing when the route request is posed.</returns>
    Task OpenAsync(NotificationRow row, CancellationToken cancellationToken = default);
}

/// <summary>
/// One notification event: the structured facts its kind names, plus the
/// digest state (a coalesced burst is ONE notification whose
/// <see cref="Count"/> counts its events and whose per-event fields carry
/// the newest event's facts). Fields not meaningful for a kind stay at
/// their unused defaults — the static factories and
/// <see cref="ComposeDigest"/> are the only writers, so a consumer never
/// reads a fabricated payload. Text is composed at display time from the
/// localization catalog (the row renderer's job), never carried inside
/// the event.
/// </summary>
/// <param name="Kind">What happened (the renderer's vocabulary key rides the kind).</param>
/// <param name="Route">The click target the kind maps to.</param>
/// <param name="Count">1 for a raw event; the burst size for a digest.</param>
/// <param name="OccurredUtc">The UTC moment the newest event in the notification occurred.</param>
/// <param name="SymbolName">The normalized symbol behind a trade outcome; empty for the other kinds.</param>
/// <param name="AdapterName">The adapter display name behind an adapter-state event; empty for the other kinds.</param>
/// <param name="AdapterState">The adapter's strip state its notification reports; <see cref="AdapterHealthState.Unsupervised"/> (the honest "no transport fact rides here") for the other kinds.</param>
/// <param name="Side">The fill's side (Buy/Sell) for a fill; unused (a plain Buy placeholder) for the rest.</param>
/// <param name="Quantity">The fill's traded quantity for a fill, the fired observation's price for an alert fire; unused (zero) for the rest.</param>
/// <param name="Reason">The rejection reason for a rejection; empty for the rest.</param>
/// <param name="Alert">The fired alert's definition for an alert fire (the row composes the alert's own condition line from it); null for the other kinds.</param>
public sealed record Notification(
    NotificationKind Kind,
    NotificationRoute Route,
    int Count,
    DateTimeOffset OccurredUtc,
    string SymbolName,
    string AdapterName,
    AdapterHealthState AdapterState,
    OrderSide Side,
    decimal Quantity,
    string Reason,
    AlertDefinition? Alert = null)
{
    /// <summary>The digest marker: a count above one renders the burst badge.</summary>
    public bool IsDigest => Count > 1;

    #region Factories

    /// <summary>
    /// One raw notification about an accepted order's terminal outcome the
    /// journal recorded (fill, cancel, rejection), straight from the row's
    /// facts: the fill carries the traded quantity, the rejection its
    /// adapter text, a cancellation the bare outcome.
    /// </summary>
    public static Notification ForJournalOutcome(
        NotificationKind kind,
        DateTimeOffset occurredUtc,
        string symbolName,
        OrderSide side,
        decimal quantity,
        string reason = "") => new(
            kind,
            NotificationRouteMap.Map(kind),
            Count: 1,
            OccurredUtc: occurredUtc,
            SymbolName: symbolName,
            AdapterName: string.Empty,
            AdapterState: AdapterHealthState.Unsupervised,
            Side: side,
            Quantity: quantity,
            Reason: reason);

    /// <summary>One raw adapter-state notification from the T-098 fold.</summary>
    public static Notification ForAdapterState(
        string adapterName,
        AdapterHealthState state,
        DateTimeOffset occurredUtc) => new(
            NotificationKind.AdapterState,
            NotificationRouteMap.Map(NotificationKind.AdapterState),
            Count: 1,
            OccurredUtc: occurredUtc,
            SymbolName: string.Empty,
            AdapterName: adapterName,
            AdapterState: state,
            Side: OrderSide.Buy,
            Quantity: 0m,
            Reason: string.Empty);

    /// <summary>The system Do-Not-Disturb summary: the count of missed events (their rows already live in history).</summary>
    public static Notification ForDoNotDisturbSummary(
        int missedCount,
        DateTimeOffset occurredUtc) => new(
            NotificationKind.DoNotDisturbSummary,
            NotificationRouteMap.Map(NotificationKind.DoNotDisturbSummary),
            Count: missedCount,
            OccurredUtc: occurredUtc,
            SymbolName: string.Empty,
            AdapterName: string.Empty,
            AdapterState: AdapterHealthState.Unsupervised,
            Side: OrderSide.Buy,
            Quantity: 0m,
            Reason: string.Empty);

    /// <summary>
    /// One raw alert-fire notification (the T-204 bridge): the fired
    /// definition rides along so the display row composes the alert's own
    /// condition line from it, and the observation's price is the
    /// structured fired fact.
    /// </summary>
    public static Notification ForAlertFire(
        AlertDefinition definition,
        AlertObservation observation) => new(
            NotificationKind.AlertFired,
            NotificationRouteMap.Map(NotificationKind.AlertFired),
            Count: 1,
            OccurredUtc: observation.OccurredUtc,
            SymbolName: definition.SymbolName,
            AdapterName: string.Empty,
            AdapterState: AdapterHealthState.Unsupervised,
            Side: OrderSide.Buy,
            Quantity: observation.Price,
            Reason: string.Empty,
            Alert: definition);

    /// <summary>
    /// Fold events of ONE kind arriving inside one coalescing window into
    /// their digest: the burst badge counts them, the per-event fields are
    /// the newest event's facts (the burst's "headline" preview) and the
    /// occurrence moment is that newest event's own moment.
    /// </summary>
    public static Notification ComposeDigest(IReadOnlyList<Notification> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException("A digest composes only from a non-empty batch.", nameof(events));
        }

        var newest = events[events.Count - 1];
        var sharedKind = SharedKind(events[0], newest);
        return new(
            sharedKind,
            NotificationRouteMap.Map(sharedKind),
            Count: events.Count,
            OccurredUtc: newest.OccurredUtc,
            SymbolName: newest.SymbolName,
            AdapterName: newest.AdapterName,
            AdapterState: newest.AdapterState,
            Side: newest.Side,
            Quantity: newest.Quantity,
            Reason: newest.Reason,
            Alert: newest.Alert);
    }

    #endregion Factories

    /// <summary>
    /// The digest's kind: its batch's shared kind, verified rather than
    /// assumed — a mismatched batch is a pipeline design fault, never a
    /// silently substituted kind.
    /// </summary>
    private static NotificationKind SharedKind(Notification first, Notification newest) =>
        EqualityComparer<NotificationKind>.Default.Equals(first.Kind, newest.Kind)
            ? first.Kind
            : throw new InvalidOperationException(
                $"A digest composes from ONE kind; the batch mixes {first.Kind} with {newest.Kind}.");
}
