namespace ZettaTrade.App.ViewModels;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// One notification row for display (T-097): the toast stack and the
/// notification history window render the same shape, only the
/// presentation differs — so one row serves both. The text composes AT
/// ARRIVAL from the localization catalog against the event's structured
/// facts (a toast row lives for seconds and the history window rebuilds
/// its rows on every open — the snapshot discipline the order history
/// rows already apply), so the row never re-renders on a language switch
/// that lands after its text was composed.
/// </summary>
public sealed class NotificationRow : ReactiveObject
{
    #region Resx Keys

    /// <summary>The fill line: side word, quantity, symbol.</summary>
    private const string FillKey = "Notif.Fill";

    /// <summary>The fill digest line: burst count + the newest fill's short line.</summary>
    private const string FillDigestKey = "Notif.FillDigest";

    /// <summary>The cancel line without a symbol (the journal could not recover it).</summary>
    private const string CancelKey = "Notif.Cancel";

    /// <summary>The cancel line with the symbol.</summary>
    private const string CancelOnKey = "Notif.CancelOn";

    /// <summary>The cancel digest line: burst count + the newest cancel's symbol frame.</summary>
    private const string CancelDigestKey = "Notif.CancelDigest";

    /// <summary>The rejection line: symbol + the adapter's own reason text.</summary>
    private const string RejectKey = "Notif.Reject";

    /// <summary>The rejection digest line: burst count + the newest rejection's reason.</summary>
    private const string RejectDigestKey = "Notif.RejectDigest";

    /// <summary>The adapter-state lines, one per strip state the toasts announce.</summary>
    private const string AdapterRecoveredKey = "Notif.AdapterRecovered";
    private const string AdapterReconnectingKey = "Notif.AdapterReconnecting";
    private const string AdapterDownKey = "Notif.AdapterDown";

    /// <summary>The Do-Not-Disturb summary line: the missed-event count (history rows already carry the events).</summary>
    private const string DndSummaryKey = "Notif.DndSummary";

    /// <summary>The alert-fire line: the symbol beside the alert's own condition line (the T-204 bridge).</summary>
    private const string AlertFiredKey = "Notif.AlertFired";

    /// <summary>The alert-fire digest line: burst count + the newest fire's line.</summary>
    private const string AlertFiredDigestKey = "Notif.AlertFiredDigest";

    /// <summary>The update announcement line: the available release's display version.</summary>
    private const string UpdateAvailableKey = "Notif.UpdateAvailable";

    /// <summary>One unknown value's rendering: the honest localized dash (the fee column's precedent).</summary>
    private const string UnknownValueKey = "Fees.Unknown";

    /// <summary>Quantity texts render invariant (the money-cell source rules).</summary>
    private const string DecimalFormat = "F8";

    #endregion Resx Keys

    #region Private Fields

    private readonly ILocalizationManager _localization;

    /// <summary>
    /// The route sink the open command hands the row to; null for the
    /// read-only mirror rows (the notification history window renders the
    /// same shape without the activation affordance).
    /// </summary>
    private readonly INotificationRouteSink? _sink;

    #endregion Private Fields

    #region Properties

    /// <summary>The structured event behind the row (the badge and the route read it).</summary>
    public Notification Source { get; }

    /// <summary>The composed one-line summary (localized at compose time, at the event's arrival).</summary>
    public string Summary { get; }

    /// <summary>
    /// The occurrence moment's local wall-clock prefix (the shared capture
    /// clock the sizer's taken-at snapshot rides).
    /// </summary>
    public string Moment { get; }

    /// <summary>The digest count (1 for a raw event, the burst size for a digest).</summary>
    public int Count => Source.Count;

    /// <summary>Whether the row renders the burst badge (a digest).</summary>
    public bool HasCountBadge => Source.IsDigest;

    /// <summary>
    /// Whether the row resolves into a panel (the click's pointer
    /// affordance); a no-route toast dismisses on click and carries
    /// nothing else to render.
    /// </summary>
    public bool HasRoute => Source.Route != NotificationRoute.None;

    /// <summary>
    /// Activate the row's route: dismiss the toast and open its source
    /// panel (the sink lands it). Null on the read-only mirror rows — the
    /// history window never binds this.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid>? OpenCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Build one display row: the summary and the moment compose right
    /// here from the event's facts, and the open command wires through the
    /// route sink only when one is supplied (a read-only mirror row
    /// carries none).
    /// </summary>
    /// <param name="source">The structured event the row renders.</param>
    /// <param name="localization">The localization catalog the summary composes from.</param>
    /// <param name="sink">The route sink the open command hands the row to; null on read-only rows.</param>
    private NotificationRow(
        Notification source,
        ILocalizationManager localization,
        INotificationRouteSink? sink = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _sink = sink;

        Summary = ComposeSummary();
        Moment = Source.OccurredUtc.ToLocalTime().ToString(
            TimeFormats.NotificationMoment,
            CultureInfo.InvariantCulture);
        OpenCommand = sink is null
            ? null
            : ReactiveCommand.CreateFromTask<RxVoid, RxVoid>((_, cancellationToken) => OpenRouteAsync(cancellationToken));
    }

    /// <summary>Create a toasting row: the open command rides the supplied route sink.</summary>
    /// <param name="source">The structured event the row renders.</param>
    /// <param name="sink">The route sink the open command hands the row to.</param>
    /// <param name="localization">The localization catalog the summary composes from.</param>
    /// <returns>The toasting row.</returns>
    public static NotificationRow ForToasting(
        Notification source,
        INotificationRouteSink sink,
        ILocalizationManager localization) =>
        new(source, localization, sink ?? throw new ArgumentNullException(nameof(sink)));

    /// <summary>Create a read-only mirror row (no open command; the history window renders it).</summary>
    /// <param name="source">The structured event the row renders.</param>
    /// <param name="localization">The localization catalog the summary composes from.</param>
    /// <returns>The mirror row.</returns>
    public static NotificationRow ForDisplay(Notification source, ILocalizationManager localization) =>
        new(source, localization);

    #endregion Constructor

    #region Commands

    /// <summary>Hand the row to its route sink (the host dismisses and lands the route; a no-route row's dismissal is the whole action).</summary>
    private async Task<RxVoid> OpenRouteAsync(CancellationToken cancellationToken)
    {
        if (_sink is not null)
        {
            await _sink.OpenAsync(this, cancellationToken);
        }

        return RxVoid.Default;
    }

    #endregion Commands

    #region Text Composition

    /// <summary>Compose the row's summary per the event's kind; an unseen kind is an argument fault, never a silent substitute label.</summary>
    private string ComposeSummary() => Source.Kind switch
    {
        NotificationKind.OrderFill when !Source.IsDigest => _localization.GetString(
            FillKey,
            _localization.GetString($"Order.{Source.Side}"),
            Source.Quantity.ToString(DecimalFormat, CultureInfo.InvariantCulture),
            Source.SymbolName),
        NotificationKind.OrderFill => _localization.GetString(
            FillDigestKey,
            Source.Count.ToString(CultureInfo.InvariantCulture),
            _localization.GetString(
                FillKey,
                _localization.GetString($"Order.{Source.Side}"),
                Source.Quantity.ToString(DecimalFormat, CultureInfo.InvariantCulture),
                Source.SymbolName)),
        NotificationKind.OrderCancel when !Source.IsDigest && Source.SymbolName.Length == 0 =>
            _localization.GetString(CancelKey),
        NotificationKind.OrderCancel when !Source.IsDigest =>
            _localization.GetString(CancelOnKey, Source.SymbolName),
        NotificationKind.OrderCancel => _localization.GetString(
            CancelDigestKey,
            Source.Count.ToString(CultureInfo.InvariantCulture),
            Source.SymbolName),
        NotificationKind.OrderReject when !Source.IsDigest => _localization.GetString(
            RejectKey,
            Source.SymbolName,
            SourceReasonText()),
        NotificationKind.OrderReject => _localization.GetString(
            RejectDigestKey,
            Source.Count.ToString(CultureInfo.InvariantCulture),
            _localization.GetString(RejectKey, Source.SymbolName, SourceReasonText())),
        NotificationKind.AdapterState => Source.AdapterState switch
        {
            AdapterHealthState.Connected => _localization.GetString(AdapterRecoveredKey, Source.AdapterName),
            AdapterHealthState.Reconnecting => _localization.GetString(AdapterReconnectingKey, Source.AdapterName),
            AdapterHealthState.Down => _localization.GetString(AdapterDownKey, Source.AdapterName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(Source),
                Source.AdapterState,
                "The adapter state has no toast vocabulary."),
        },
        NotificationKind.DoNotDisturbSummary => _localization.GetString(
            DndSummaryKey,
            Source.Count.ToString(CultureInfo.InvariantCulture)),
        NotificationKind.AlertFired when Source.Alert is { } alert && !Source.IsDigest => _localization.GetString(
            AlertFiredKey,
            Source.SymbolName,
            AlertConditionText.Compose(alert, _localization)),
        NotificationKind.AlertFired when Source.Alert is { } alert => _localization.GetString(
            AlertFiredDigestKey,
            Source.Count.ToString(CultureInfo.InvariantCulture),
            _localization.GetString(
                AlertFiredKey,
                Source.SymbolName,
                AlertConditionText.Compose(alert, _localization))),
        NotificationKind.UpdateAvailable => _localization.GetString(
            UpdateAvailableKey,
            Source.VersionLabel),
        _ => throw new ArgumentOutOfRangeException(
            nameof(Source),
            Source.Kind,
            "The notification kind has no row vocabulary."),
    };

    /// <summary>
    /// The rejection reason the row renders: the adapter's own text, or
    /// the honest localized dash when the journal recorded no reason.
    /// </summary>
    private string SourceReasonText() => Source.Reason.Length == 0
        ? _localization.GetString(UnknownValueKey)
        : Source.Reason;

    #endregion Text Composition
}
