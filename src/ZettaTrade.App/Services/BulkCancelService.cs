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
/// Bulk order management for the orders panel (T-076): cancel every working
/// order on the workspace's active symbol in ONE confirmed pass.
/// <para>
/// Scope: the symbol's set on the current trading context — the panel rides
/// a single <see cref="ITradingAdapter"/> and every order carries its own
/// venue, so the workspace symbol filter picking the set off that same
/// adapter IS "the symbol's orders on the current exchange". The pass
/// re-reads the adapter's open orders at click time (the display rows may be
/// stale and are never the source of the target set) and commands ONLY
/// orders still in a working status; orders whose per-row pipeline is
/// already in flight (a T-031 cancel or a T-075 amend) are skipped and
/// reported, never double-commanded.
/// </para>
/// <para>
/// One confirmation precedes the pass — composed through
/// <see cref="OrderConfirmationComposer"/> with the symbol, the count, the
/// total remaining notional (quantity minus any fill, priced at each
/// order's own anchor price) and the paper/live tag; a refusal leaves every
/// order untouched. Every item rides the same cancel command path as the
/// per-row cancel and the MCP cancel tool:
/// <c>CancelOrderAsync(order.Symbol, order.Id)</c> — the order's own symbol
/// is always carried, no risk bypass, no new adapter capability.
/// </para>
/// <para>
/// The per-item pass is best effort: one adapter fault turns into that
/// item's failed report and never stops the rest; the order-history
/// bookkeeping after each accepted cancel is equally best effort (the
/// adapter has already cancelled the order, a persistence fault can never
/// undo it).
/// </para>
/// </summary>
public sealed class BulkCancelService
{
    #region Resx Keys

    private const string NoSymbolKey = "Positions.BulkNoSymbol";
    private const string NothingWorkingKey = "Positions.BulkCancelNothing";
    private const string DoneKey = "Positions.BulkCancelDone";
    private const string FailedItemsKey = "Positions.BulkCancelFailedItems";
    private const string AbortedKey = "Positions.BulkCancelAborted";

    /// <summary>
    /// The summary's trailing argument when nothing failed: a bare full stop
    /// (the template leaves the tail slot open for the failed-items clause).
    /// </summary>
    private const string FailedItemsNone = ".";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly WorkspaceSymbolService _workspaceSymbol;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<BulkCancelService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the bulk cancel service over the trading face, the
    /// confirmation gate, the shared composer, the workspace symbol snapshot
    /// and the persistent order history.
    /// </summary>
    public BulkCancelService(
        ITradingAdapter trading,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        WorkspaceSymbolService workspaceSymbol,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<BulkCancelService> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _workspaceSymbol = workspaceSymbol ?? throw new ArgumentNullException(nameof(workspaceSymbol));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Cancel every working order on the workspace's active symbol in one
    /// confirmed, best-effort pass. <paramref name="inFlightOrderIds"/> names
    /// the orders a per-row panel pipeline already owns (a cancel or an amend
    /// mid-flight): they are skipped and reported honestly — never
    /// double-commanded.
    /// </summary>
    /// <param name="inFlightOrderIds">The order ids to skip (may be empty).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The attempt's outcome with its localized, panel-renderable lines.</returns>
    public async Task<BulkCancelResult> CancelWorkingOrdersAsync(
        IReadOnlyCollection<string>? inFlightOrderIds,
        CancellationToken ct = default)
    {
        var skipIds = new HashSet<string>(inFlightOrderIds ?? [], StringComparer.Ordinal);

        // Shared across the flow, so a mid-pass abort still reports exactly
        // what was reached: the skipped rows, the resolved items, and the
        // explicit NotAttempted tail for everything never commanded.
        Symbol? symbol = null;
        List<(Order Order, bool IsInFlight)> targets = [];
        var reports = new List<BulkCancelReport>();

        try
        {
            // Scope: the workspace symbol AS IT STANDS at click time. A
            // symbol-less workspace is an explicit localized rejection —
            // never a guessed "everything" scope (canon: cancel_order always
            // requires the symbol).
            symbol = _workspaceSymbol.Current;
            if (symbol is null)
            {
                _logger.LogInformation("Bulk cancel: no workspace symbol; the pass is rejected");
                return BulkCancelResult.Fail(symbol, reports, _localization.GetString(NoSymbolKey));
            }

            // The fresh adapter read is the whole truth at click time; the
            // panel rows may be stale by seconds or minutes. A mixed
            // working/cancelled set cancels ONLY the working statuses.
            var open = await _trading.GetOpenOrdersAsync(ct);
            targets = open
                .Where(order => OrderIsOnSymbol(order, symbol) && IsWorkingStatus(order.Status))
                .Select(order => (order, skipIds.Contains(order.Id)))
                .ToList();
            var commandable = targets.Where(target => !target.IsInFlight).ToList();
            var skippedCount = targets.Count - commandable.Count;

            if (targets.Count == 0)
            {
                _logger.LogInformation(
                    "Bulk cancel: no working orders on {Symbol}; the pass reports the empty state",
                    symbol.Normalized);
                return BulkCancelResult.Fail(
                    symbol,
                    reports,
                    _localization.GetString(NothingWorkingKey, symbol.Normalized));
            }

            if (commandable.Count == 0)
            {
                // Everything working was already owned by an in-flight panel
                // pipeline: report the skips honestly (the adapter's read
                // order), send nothing.
                _logger.LogInformation(
                    "Bulk cancel: {Symbol} has only in-flight working orders ({Skipped}); nothing commanded",
                    symbol.Normalized,
                    skippedCount);
                reports.AddRange(SkipReports(targets));
                return BulkCancelResult.Done(symbol, reports, ComposeSummary(symbol, reports));
            }

            // ONE confirmation gates the whole pass: the symbol, the count,
            // the total remaining notional and the paper/live tag. A refusal
            // leaves every order untouched.
            var totalNotional = commandable.Sum(target => RemainingNotional(target.Order));
            if (!await _confirmation.RequestConfirmationAsync(
                    _composer.ForBulkCancel(
                        symbol,
                        commandable.Count,
                        totalNotional,
                        IsPaperVenue(),
                        _trading.Name),
                    ct))
            {
                _logger.LogInformation(
                    "Bulk cancel: the user refused the confirmation on {Symbol}; nothing touched",
                    symbol.Normalized);
                return BulkCancelResult.Declined(symbol);
            }

            // The pass walks the fresh read IN ORDER: an in-flight row gets
            // its skip report at exactly its position in the set, every free
            // row rides the cancel command path in turn — one item's fault
            // never stops the others.
            foreach (var (order, inFlight) in targets)
            {
                if (inFlight)
                {
                    reports.Add(new BulkCancelReport
                    {
                        OrderId = order.Id,
                        Symbol = order.Symbol,
                        Outcome = BulkCancelReportOutcome.SkippedInFlight,
                    });
                    continue;
                }

                try
                {
                    // The same command path as the per-row cancel and the
                    // MCP cancel_order tool: the order's own symbol is
                    // always carried (the Symbol-first contract).
                    await _trading.CancelOrderAsync(order.Symbol, order.Id, ct);
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                    {
                        _logger.LogInformation(
                            ex,
                            "Bulk cancel: order {OrderId} on {Symbol} was not completed (cancelled)",
                            order.Id,
                            order.Symbol.Normalized);
                    }
                    else
                    {
                        _logger.LogError(
                            ex,
                            "Bulk cancel: order {OrderId} on {Symbol} failed",
                            order.Id,
                            order.Symbol.Normalized);
                    }

                    reports.Add(FailReport(order, ex.Message));
                    continue; // one fault never stops the others
                }

                reports.Add(new BulkCancelReport
                {
                    OrderId = order.Id,
                    Symbol = order.Symbol,
                    Outcome = BulkCancelReportOutcome.Cancelled,
                });

                // Record the cancellation in the persistent order history.
                // Best effort: the adapter has already cancelled the order, so
                // a persistence fault must never undo or fail the pass.
                try
                {
                    await _orderHistory.MarkCancelledAsync(order.ExchangeId, order.Id, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Bulk cancel: failed to mark {OrderId} cancelled in the order history",
                        order.Id);
                }
            }

            return BulkCancelResult.Done(symbol, reports, ComposeSummary(symbol, reports));
        }
        catch (Exception ex)
        {
            // Broader than the adapter's validation refusals: the fresh read
            // and the confirmation ride the same surface a network request
            // does, and a stopped pass (the flow abandoned while the panel
            // closes) resolves into the localized failure line too. Whatever
            // was reached stays in the reports; every commanded set member
            // without a terminal report becomes the explicit NotAttempted
            // tail — the order list never lies.
            if (ex is OperationCanceledException)
            {
                _logger.LogInformation(ex, "Bulk cancel: the pass was cancelled before completing");
            }
            else
            {
                _logger.LogError(ex, "Bulk cancel: the pass failed before completing");
            }

            // Whatever the pass never reached stays honest in the reports:
            // an in-flight row keeps its skip line (owned elsewhere), a free
            // row declares not-attempted — the order list never lies.
            foreach (var (order, inFlight) in targets)
            {
                if (reports.Any(report => report.OrderId == order.Id))
                {
                    continue;
                }

                reports.Add(new BulkCancelReport
                {
                    OrderId = order.Id,
                    Symbol = order.Symbol,
                    Outcome = inFlight
                        ? BulkCancelReportOutcome.SkippedInFlight
                        : BulkCancelReportOutcome.NotAttempted,
                });
            }

            return BulkCancelResult.Fail(
                symbol,
                reports,
                _localization.GetString(AbortedKey, ex.Message));
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The localized summary line: the per-outcome counts, plus the failed
    /// items' reasons in the trailing slot when there are any.
    /// </summary>
    private string ComposeSummary(Symbol symbol, IReadOnlyList<BulkCancelReport> reports)
    {
        var cancelled = reports.Count(report => report.Outcome == BulkCancelReportOutcome.Cancelled);
        var skipped = reports.Count(report => report.Outcome == BulkCancelReportOutcome.SkippedInFlight);
        var failed = reports.Where(report => report.Outcome == BulkCancelReportOutcome.Failed).ToArray();
        var notAttempted = reports.Count(report => report.Outcome == BulkCancelReportOutcome.NotAttempted);

        var failedTail = failed.Length == 0
            ? FailedItemsNone
            : _localization.GetString(
                FailedItemsKey,
                string.Join("; ", failed.Select(report => $"{report.OrderId}: {report.Message}")));

        return _localization.GetString(
            DoneKey,
            symbol.Normalized,
            cancelled,
            skipped,
            failed.Length,
            notAttempted,
            failedTail);
    }

    /// <summary>A failed item's report carrying the raw fault text for the summary tail.</summary>
    private static BulkCancelReport FailReport(Order order, string message) => new()
    {
        OrderId = order.Id,
        Symbol = order.Symbol,
        Outcome = BulkCancelReportOutcome.Failed,
        Message = message,
    };

    /// <summary>Skip reports over the targets, keeping the adapter's read order.</summary>
    private static IEnumerable<BulkCancelReport> SkipReports(
        IEnumerable<(Order Order, bool IsInFlight)> targets) =>
        targets.Select(target => new BulkCancelReport
        {
            OrderId = target.Order.Id,
            Symbol = target.Order.Symbol,
            Outcome = BulkCancelReportOutcome.SkippedInFlight,
        });

    /// <summary>
    /// The paper/live tag decision: the shipped paper venue tags the
    /// confirmation with the localized paper tag (the stats' venue filter
    /// matches on the same adapter id constant); a live venue displays its
    /// own name.
    /// </summary>
    private bool IsPaperVenue() =>
        string.Equals(_trading.Id, PaperTradingAdapter.ExchangeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One order's remaining notional: its unfilled quantity priced at the
    /// order's own anchor price — the same anchor the risk gates evaluate
    /// (a pending stop names its trigger). Nothing is filled in when the
    /// anchor is missing: orders always carry one.
    /// </summary>
    private static decimal RemainingNotional(Order order) =>
        (order.Quantity.Value - order.FilledQuantity.Value) * order.Price.Value;

    /// <summary>The symbol filter: normalized comparison, exchange formats never leak into the match.</summary>
    private static bool OrderIsOnSymbol(Order order, Symbol symbol) =>
        order.Symbol.Normalized == symbol.Normalized;

    /// <summary>
    /// The working statuses a cancel can still act on — the same set the
    /// panel rows gate their Cancel button by (Pending, Submitted,
    /// PartiallyFilled). Anything the adapter already serves as terminal is
    /// never commanded, so a mixed working/cancelled set stays honest.
    /// </summary>
    private static bool IsWorkingStatus(OrderStatus status) =>
        status is OrderStatus.Pending
            or OrderStatus.Submitted
            or OrderStatus.PartiallyFilled;

    #endregion Private Methods
}
