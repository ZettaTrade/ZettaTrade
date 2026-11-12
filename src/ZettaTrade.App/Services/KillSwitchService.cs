namespace ZettaTrade.App.Services;

using System.Linq;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The kill switch (T-188): one user action that cancels every working
/// order and closes every open position across the app's trading venues,
/// behind ONE confirmation listing what will be cancelled and closed.
/// <para>
/// The sweep is the T-076 discipline widened to positions and venues: the
/// fresh adapter reads at action time are the whole truth (the panel rows
/// are never a source), the plan is confirmed ONCE
/// (<see cref="OrderConfirmationComposer.ForKillSwitch"/> — the totals and
/// the per-venue listing), and the execution is per-item best effort — one
/// item's fault turns into that item's failed report and never stops the
/// rest. Nothing fires without the confirmation; a refusal touches
/// nothing. When every venue reads empty the sweep reports the localized
/// nothing WITHOUT a confirmation (the T-187 clear-no-op precedent).
/// </para>
/// <para>
/// Every venue is commanded: the panic action's job is "flat everywhere",
/// so a venue keeps no survivor behind a lane or picker subtlety. A venue
/// whose read faults reports itself unreachable and the sweep continues
/// with the rest. Cancellations ride the same command path as the per-row
/// cancel and the MCP cancel tool — <c>CancelOrderAsync(order.Symbol,
/// order.Id)</c>, the order's own symbol always carried. Position closes
/// ride the same validated shape the close action uses — one reduce-only
/// market order sized against the position AS IT IS at the fresh re-read
/// (an exit that vanished reports already flat; a changed one sizes by the
/// fresh facts, the reduce-only flag capping what the venue reports) —
/// validated, risk-checked (a rejection never reaches the venue) and
/// never per-item confirmed: the ONE sweep confirmation covers them all,
/// exactly like the bracket's and the reverse's single summary.
/// </para>
/// <para>
/// The bookkeeping after each accepted item is best effort (the adapter
/// has already acted, a persistence fault can never undo it): a cancel
/// marks the order history, a placed exit lands in the session journal and
/// the order history.
/// </para>
/// </summary>
public sealed class KillSwitchService
{
    #region Resx Keys

    private const string NothingKey = "Trading.KillSwitchNothing";
    private const string VenueUnreachableKey = "Trading.KillSwitchVenueUnreachableFormat";
    private const string VenueDoneKey = "Trading.KillSwitchVenueDoneFormat";
    private const string ClosedTailKey = "Trading.KillSwitchClosedTailFormat";
    private const string ClosedItemKey = "Trading.KillSwitchClosedItemFormat";
    private const string FailedItemsKey = "Trading.KillSwitchFailedItemsFormat";
    private const string AbortedKey = "Trading.KillSwitchAbortedFormat";
    private const string TagPaperKey = "Positions.BulkTagPaper";

    /// <summary>
    /// The summary's trailing argument when nothing failed: a bare full stop
    /// (the template leaves the tail slot open for the failed-items clause).
    /// </summary>
    private const string FailedItemsNone = ".";

    #endregion Resx Keys

    #region Private Fields

    private readonly KillSwitchVenues _venues;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<KillSwitchService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the kill switch over the venue set and the mandatory order
    /// pipeline's gates (the exits never bypass the risk manager).
    /// </summary>
    public KillSwitchService(
        KillSwitchVenues venues,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<KillSwitchService> logger)
    {
        _venues = venues ?? throw new ArgumentNullException(nameof(venues));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Run the sweep: the fresh per-venue reads build the plan, the ONE
    /// confirmation gates it, and the per-item best-effort execution
    /// cancels the orders and closes the positions it named. A refusal
    /// touches nothing; a mid-sweep fault reports whatever was reached.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The attempt's outcome with its per-item reports and summary.</returns>
    public async Task<KillSwitchResult> ExecuteAsync(CancellationToken ct = default)
    {
        List<KillSwitchVenuePlan> plans = [];
        List<KillSwitchItemReport> reports = [];
        List<string> unreachableNotes = [];

        try
        {
            // Phase 1 — the plan: the fresh reads are the whole truth at
            // action time. A venue whose read faults names itself
            // unreachable and the sweep continues; its orders and positions
            // stay unknown to the plan (the honest unknown, never a guess).
            foreach (var venue in _venues.Venues)
            {
                try
                {
                    var orders = (await venue.GetOpenOrdersAsync(ct))
                        .Where(order => IsWorkingStatus(order.Status))
                        .ToList();
                    var positions = (await venue.GetPositionsAsync(ct))
                        .Where(position => position.Quantity.Value > decimal.Zero)
                        .ToList();
                    plans.Add(new KillSwitchVenuePlan(VenueName(venue), venue, orders, positions));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Kill switch: the plan read of {VenueName} failed; the venue reports unreachable",
                        venue.Name);
                    unreachableNotes.Add(_localization.GetString(
                        VenueUnreachableKey,
                        VenueName(venue),
                        ex.Message));
                }
            }

            var totalOrders = plans.Sum(plan => plan.Orders.Count);
            var totalPositions = plans.Sum(plan => plan.Positions.Count);

            if (totalOrders == 0 && totalPositions == 0)
            {
                // Nothing to command anywhere: the explicit localized
                // nothing WITHOUT a confirmation (the T-187 clear-no-op
                // precedent). The unreachable notes ride the summary so the
                // unknown venues stay visible.
                _logger.LogInformation(
                    "Kill switch: nothing working and nothing open across {ReadableVenues} readable venue(s); the sweep reports the empty state",
                    plans.Count);
                return KillSwitchResult.Done(
                    [],
                    ComposeNothingSummary(unreachableNotes));
            }

            // ONE confirmation gates the whole sweep: the totals and the
            // per-venue listing. A refusal leaves every venue untouched.
            if (!await _confirmation.RequestConfirmationAsync(
                    _composer.ForKillSwitch(plans),
                    ct))
            {
                _logger.LogInformation(
                    "Kill switch: the user refused the confirmation; nothing touched");
                return KillSwitchResult.Declined();
            }

            // Phase 2 — the execution, per venue in plan order: the cancels
            // first (stop new risk), then the closes. One item's fault never
            // stops the others.
            foreach (var plan in plans)
            {
                await SweepVenueOrdersAsync(plan, reports, ct);
                await SweepVenuePositionsAsync(plan, reports, ct);
            }

            return KillSwitchResult.Done(reports, ComposeSummary(reports, unreachableNotes, aborted: false, abortReason: null));
        }
        catch (Exception ex)
        {
            // Broader than the per-item faults: a plan read fault on the
            // flow's own surface or a stopped sweep resolves into the
            // localized abort line too. Whatever was commanded keeps its
            // report; every confirmed item without one becomes the explicit
            // NotAttempted tail — the item list never lies.
            if (ex is OperationCanceledException)
            {
                _logger.LogInformation(ex, "Kill switch: the sweep was cancelled before completing");
            }
            else
            {
                _logger.LogError(ex, "Kill switch: the sweep failed before completing");
            }

            foreach (var plan in plans)
            {
                foreach (var order in plan.Orders)
                {
                    if (reports.Any(report => report.Symbol.Normalized == order.Symbol.Normalized
                            && report.OrderId == order.Id))
                    {
                        continue;
                    }

                    reports.Add(new KillSwitchItemReport
                    {
                        VenueName = plan.VenueName,
                        Symbol = order.Symbol,
                        OrderId = order.Id,
                        Outcome = KillSwitchItemOutcome.NotAttempted,
                    });
                }

                foreach (var position in plan.Positions)
                {
                    if (reports.Any(report => report.Symbol.Normalized == position.Symbol.Normalized
                            && report.OrderId is null))
                    {
                        continue;
                    }

                    reports.Add(new KillSwitchItemReport
                    {
                        VenueName = plan.VenueName,
                        Symbol = position.Symbol,
                        Outcome = KillSwitchItemOutcome.NotAttempted,
                    });
                }
            }

            return KillSwitchResult.Failed(
                reports,
                ComposeSummary(reports, unreachableNotes, aborted: true, abortReason: ex.Message));
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Cancel one venue's planned working orders, in the plan's read order.
    /// The same command path as the per-row cancel and the MCP cancel tool:
    /// the order's own symbol is always carried. One fault produces exactly
    /// its own failed report; the accepted cancellations mark the persistent
    /// order history best effort.
    /// </summary>
    private async Task SweepVenueOrdersAsync(
        KillSwitchVenuePlan plan,
        List<KillSwitchItemReport> reports,
        CancellationToken ct)
    {
        foreach (var order in plan.Orders)
        {
            try
            {
                await plan.Venue.CancelOrderAsync(order.Symbol, order.Id, ct);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    _logger.LogInformation(
                        ex,
                        "Kill switch: order {OrderId} on {Symbol} was not completed (cancelled)",
                        order.Id,
                        order.Symbol.Normalized);
                }
                else
                {
                    _logger.LogError(
                        ex,
                        "Kill switch: order {OrderId} on {Symbol} failed to cancel",
                        order.Id,
                        order.Symbol.Normalized);
                }

                reports.Add(new KillSwitchItemReport
                {
                    VenueName = plan.VenueName,
                    Symbol = order.Symbol,
                    OrderId = order.Id,
                    Outcome = KillSwitchItemOutcome.Failed,
                    Message = ex.Message,
                });
                continue; // one fault never stops the others
            }

            reports.Add(new KillSwitchItemReport
            {
                VenueName = plan.VenueName,
                Symbol = order.Symbol,
                OrderId = order.Id,
                Outcome = KillSwitchItemOutcome.OrderCancelled,
            });

            // Best effort: the adapter has already cancelled the order, so a
            // persistence fault must never undo or fail the sweep.
            try
            {
                await _orderHistory.MarkCancelledAsync(order.ExchangeId, order.Id, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Kill switch: failed to mark {OrderId} cancelled in the order history",
                    order.Id);
            }
        }
    }

    /// <summary>
    /// Close one venue's planned positions. The fresh re-read after the
    /// cancels is the sizing truth: a vanished position reports already
    /// flat, a changed one closes by its fresh side and quantity (the
    /// reduce-only flag caps what the venue reports), and every exit rides
    /// the mandatory pipeline — validate, risk-check (a rejection never
    /// reaches the venue), place — with the journal and history bookkeeping
    /// best effort. One fault produces exactly its own failed report.
    /// </summary>
    private async Task SweepVenuePositionsAsync(
        KillSwitchVenuePlan plan,
        List<KillSwitchItemReport> reports,
        CancellationToken ct)
    {
        if (plan.Positions.Count == 0)
        {
            return;
        }

        IReadOnlyList<Position> fresh;
        try
        {
            fresh = await plan.Venue.GetPositionsAsync(ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Kill switch: the fresh position re-read of {VenueName} failed; the venue's planned positions report not attempted",
                    plan.VenueName);
            }

            foreach (var position in plan.Positions)
            {
                reports.Add(new KillSwitchItemReport
                {
                    VenueName = plan.VenueName,
                    Symbol = position.Symbol,
                    Outcome = KillSwitchItemOutcome.NotAttempted,
                });
            }

            return;
        }

        foreach (var planned in plan.Positions)
        {
            var current = fresh.FirstOrDefault(candidate =>
                candidate.Symbol.Normalized == planned.Symbol.Normalized
                && candidate.Quantity.Value > decimal.Zero);

            if (current is null)
            {
                // The position the plan confirmed is already gone (an
                // external fill landed between the confirmation and the
                // close): the goal state is already true, reported honestly.
                _logger.LogInformation(
                    "Kill switch: {Symbol} on {VenueName} has no open position left; the item reports already flat",
                    planned.Symbol.Normalized,
                    plan.VenueName);
                reports.Add(new KillSwitchItemReport
                {
                    VenueName = plan.VenueName,
                    Symbol = planned.Symbol,
                    Outcome = KillSwitchItemOutcome.AlreadyFlat,
                });
                continue;
            }

            try
            {
                var order = await ClosePositionAsync(plan, current, ct);
                reports.Add(new KillSwitchItemReport
                {
                    VenueName = plan.VenueName,
                    Symbol = current.Symbol,
                    OrderId = order.Id,
                    Outcome = KillSwitchItemOutcome.PositionClosed,
                });
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Kill switch: the close of {Symbol} on {VenueName} failed",
                        current.Symbol.Normalized,
                        plan.VenueName);
                }

                reports.Add(new KillSwitchItemReport
                {
                    VenueName = plan.VenueName,
                    Symbol = current.Symbol,
                    Outcome = KillSwitchItemOutcome.Failed,
                    Message = ex.Message,
                });
            }
        }
    }

    /// <summary>
    /// One validated, risk-checked reduce-only market exit sized by the
    /// fresh position. The anchor is the market's own freshest reference
    /// close (a bare market order carries no price and is explicitly
    /// rejected by the shipped manager); the exit side derives from the
    /// position's own side — an undefined side is never defaulted into an
    /// entry (trading-safety rule), it faults the item.
    /// </summary>
    private async Task<Order> ClosePositionAsync(
        KillSwitchVenuePlan plan,
        Position position,
        CancellationToken ct)
    {
        var reference = await MarketReference.GetLatestCloseAsync(plan.Venue, position.Symbol, ct);
        var request = new OrderRequest
        {
            Symbol = position.Symbol,
            Side = ExitSideOf(position),
            Type = OrderType.Market,
            Quantity = new Quantity(position.Quantity.Value),
            Price = reference,
            ReduceOnly = true,
        };

        try
        {
            request.Validate();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Kill switch: the built exit request for {Symbol} is invalid and was never sent to a gate",
                position.Symbol.Normalized);
            throw;
        }

        var risk = await _riskManager.CheckOrderAsync(request, ct);
        if (!risk.IsApproved)
        {
            _logger.LogWarning(
                "Kill switch: risk controls rejected the {Side} {Type} {Quantity} {Symbol} exit: {Reason}",
                request.Side,
                request.Type,
                request.Quantity.Value,
                request.Symbol.Normalized,
                risk.Reason);
            throw new InvalidOperationException(risk.Reason ?? "the risk manager refused the exit without a reason");
        }

        var order = await plan.Venue.PlaceOrderAsync(request, ct);
        _journal.RecordPlaced(order);

        try
        {
            await _orderHistory.RecordPlacedAsync(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Kill switch: failed to persist {OrderId} to the order history",
                order.Id);
        }

        return order;
    }

    /// <summary>
    /// The side that exits a position: an undefined side is never defaulted
    /// into an entry (trading-safety rule) — it faults the item.
    /// </summary>
    private static OrderSide ExitSideOf(Position position)
    {
        if (position.Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new InvalidOperationException(
                $"Unsupported position side {position.Side}; the exit side cannot be derived for {position.Symbol.Normalized}.");
        }

        return position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
    }

    /// <summary>
    /// The localized multi-line summary: an abort reason leading when the
    /// sweep stopped early, one line per venue (the per-outcome counts, the
    /// closed positions named, the failed items' reasons in the tail), then
    /// the unreachable notes. The report names what happened per venue —
    /// the item list never lies.
    /// </summary>
    private string ComposeSummary(
        IReadOnlyList<KillSwitchItemReport> reports,
        IReadOnlyList<string> unreachableNotes,
        bool aborted,
        string? abortReason)
    {
        var lines = new List<string>();

        if (aborted)
        {
            lines.Add(_localization.GetString(AbortedKey, abortReason ?? string.Empty));
        }

        foreach (var (venueName, bucket) in GroupByVenue(reports))
        {
            var cancelled = bucket.Count(report => report.Outcome == KillSwitchItemOutcome.OrderCancelled);
            var closed = bucket
                .Where(report => report.Outcome == KillSwitchItemOutcome.PositionClosed)
                .ToList();
            var closedTail = closed.Count == 0
                ? string.Empty
                : _localization.GetString(
                    ClosedTailKey,
                    string.Join(", ", closed.Select(report => _localization.GetString(
                        ClosedItemKey,
                        report.Symbol.Normalized,
                        report.OrderId ?? string.Empty))));
            var alreadyFlat = bucket.Count(report => report.Outcome == KillSwitchItemOutcome.AlreadyFlat);
            var notAttempted = bucket.Count(report => report.Outcome == KillSwitchItemOutcome.NotAttempted);
            var failed = bucket.Where(report => report.Outcome == KillSwitchItemOutcome.Failed).ToList();
            var failedTail = failed.Count == 0
                ? FailedItemsNone
                : _localization.GetString(
                    FailedItemsKey,
                    string.Join("; ", failed.Select(report => $"{ItemLabel(report)}: {report.Message}")));

            lines.Add(_localization.GetString(
                VenueDoneKey,
                venueName,
                cancelled,
                closed.Count,
                closedTail,
                alreadyFlat,
                notAttempted,
                failedTail));
        }

        lines.AddRange(unreachableNotes);

        if (lines.Count == 0)
        {
            lines.Add(_localization.GetString(NothingKey));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The nothing-to-do summary: the localized nothing, then the unreachable notes.</summary>
    private string ComposeNothingSummary(IReadOnlyList<string> unreachableNotes)
    {
        var lines = new List<string> { _localization.GetString(NothingKey) };
        lines.AddRange(unreachableNotes);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Group the reports per venue, preserving the first-seen venue order.</summary>
    private static List<(string VenueName, IReadOnlyList<KillSwitchItemReport> Reports)> GroupByVenue(
        IReadOnlyList<KillSwitchItemReport> reports)
    {
        List<(string, IReadOnlyList<KillSwitchItemReport>)> groups = [];
        var buckets = new Dictionary<string, List<KillSwitchItemReport>>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            if (!buckets.TryGetValue(report.VenueName, out var bucket))
            {
                bucket = [];
                buckets[report.VenueName] = bucket;
                groups.Add((report.VenueName, bucket));
            }

            bucket.Add(report);
        }

        return groups;
    }

    /// <summary>A failed item's label: the order id when one exists, the symbol otherwise.</summary>
    private static string ItemLabel(KillSwitchItemReport report) =>
        report.OrderId ?? report.Symbol.Normalized;

    /// <summary>
    /// The venue display name: the shipped paper venue tags the summary with
    /// the localized paper tag (the stats' venue filter matches on the same
    /// adapter id constant); a live venue displays its own name.
    /// </summary>
    private string VenueName(ITradingAdapter venue) =>
        string.Equals(venue.Id, PaperTradingAdapter.ExchangeId, StringComparison.OrdinalIgnoreCase)
            ? _localization.GetString(TagPaperKey)
            : venue.Name;

    /// <summary>
    /// The working statuses a cancel can still act on — the same set the
    /// T-076 pass gates by (Pending, Submitted, PartiallyFilled). Anything
    /// the adapter already serves as terminal is never commanded, so a
    /// mixed working/terminal set stays honest.
    /// </summary>
    private static bool IsWorkingStatus(OrderStatus status) =>
        status is OrderStatus.Pending
            or OrderStatus.Submitted
            or OrderStatus.PartiallyFilled;

    #endregion Private Methods
}
