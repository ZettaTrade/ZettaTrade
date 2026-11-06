namespace ZettaTrade.App.Services;

using System.Globalization;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The working-order amend pipeline. Every amend rides the mandatory
/// pipeline: the adapter's current state is re-read (never the stale
/// display row), the amended shape is intrinsically validated and
/// re-checked through <see cref="IRiskManager"/> (an amend is an
/// order-shape change — never a bypass), large-delta amends get a human
/// confirmation through <see cref="IConfirmationService"/>, and the replace
/// itself rides the adapter's replace seam where it exists — otherwise the
/// explicit <c>CancelOrderAsync</c> + <c>PlaceOrderAsync</c> sequence,
/// surfaced AS cancel+new (never a fake in-place replace).
/// <para>
/// The fill race resolves through state checks, not locks: the adapter is
/// re-read at replace time and the cancel (or the replace call itself on
/// replace-capable adapters) is the final atomic checkpoint — an order that
/// filled or ended meanwhile makes the adapter refuse the cancel, the fill
/// wins, is reported, and no replacement is ever placed alongside it (no
/// double entry, no silent drop). A cancel that landed while the replacement
/// placement failed is reported loudly too: the order list never lies.
/// </para>
/// </summary>
public sealed class OrderAmendService
{
    #region Resx Keys

    private const string AmendStaleKey = "Positions.AmendStale";
    private const string AmendTypeUnsupportedKey = "Positions.AmendTypeUnsupported";
    private const string AmendOcoUnsupportedKey = "Positions.AmendOcoUnsupported";
    private const string AmendInvalidKey = "Positions.AmendInvalid";
    private const string AmendQtyBelowFilledKey = "Positions.AmendQtyBelowFilled";
    private const string AmendNoChangeKey = "Positions.AmendNoChange";
    private const string AmendRejectedKey = "Positions.AmendRejected";
    private const string AmendDoneKey = "Positions.AmendDone";
    private const string AmendDoneInPlaceKey = "Positions.AmendDoneInPlace";
    private const string AmendCancelFailedKey = "Positions.AmendCancelFailed";
    private const string AmendReplaceFailedKey = "Positions.AmendReplaceFailed";
    private const string AmendFailedKey = "Positions.AmendFailed";

    #endregion Resx Keys

    #region Constants

    /// <summary>
    /// Relative notional change above which an amend asks for confirmation:
    /// |amended notional − original notional| larger than this percent of the
    /// original notional, in EITHER direction. A shrinking order rewrites
    /// risk just as much as an enlarged one, so the delta is absolute.
    /// </summary>
    public const decimal LargeAmendDeltaPercent = 25m;

    #endregion Constants

    private readonly ITradingAdapter _trading;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<OrderAmendService> _logger;

    /// <summary>
    /// Create the amend flow service.
    /// </summary>
    public OrderAmendService(
        ITradingAdapter trading,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<OrderAmendService> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Amend a working order (T-075). The confirmation rule ("when is an
    /// amend large"): an amend confirms when the order has any fill in
    /// progress (<c>FilledQuantity > 0</c> — amending rewrites the
    /// remaining exposure of a live order), or when the notional moves
    /// beyond <see cref="LargeAmendDeltaPercent"/> of the original notional
    /// in either direction; the risk manager demanding a confirmation (its
    /// own threshold) forces one as well.
    /// </summary>
    /// <param name="request">The amend request (target order + dialog edits).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome with its localized, panel-renderable message.</returns>
    public async Task<OrderAmendResult> AmendAsync(OrderAmendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var working = request.Working;

        try
        {
            // Step 1 — the fresh state of the target is authoritative; the
            // display row the user clicked can be stale by seconds or minutes.
            var fresh = await FindOpenOrderAsync(working.Id, ct);
            if (fresh is null || !IsWorkingStatus(fresh.Status))
            {
                _logger.LogInformation(
                    "Amend: order {OrderId} is no longer working; the amend was not performed",
                    working.Id);
                return OrderAmendResult.Failed(_localization.GetString(AmendStaleKey, working.Id));
            }

            // Step 2 — shape gates. Only the in-place-working types are
            // amendable; a market order executes at once and stops cannot be
            // smoothed here. OCO legs are excluded on purpose: this seam's
            // cancel+new cannot keep the either-or contract atomic, and a
            // re-placed leg joins a group whose partner is already terminal
            // (the paper adapter refuses it) — an atomically replaceable
            // adapter seam could open them later.
            if (fresh.Type is not (OrderType.Limit or OrderType.StopLimit or OrderType.TrailingStop))
            {
                _logger.LogWarning(
                    "Amend: orders of type {OrderType} cannot be amended (order {OrderId}); refused",
                    fresh.Type,
                    fresh.Id);
                return OrderAmendResult.Failed(
                    _localization.GetString(AmendTypeUnsupportedKey, fresh.Type));
            }

            if (fresh.OcoGroupId is not null)
            {
                _logger.LogWarning(
                    "Amend: OCO leg {OrderId} is refused; the cancel+new seam cannot hold the either-or contract atomically",
                    fresh.Id);
                return OrderAmendResult.Failed(
                    _localization.GetString(AmendOcoUnsupportedKey, fresh.OcoGroupId));
            }

            if (request.NewLimitPrice is { } editedPrice &&
                fresh.Type is not (OrderType.Limit or OrderType.StopLimit))
            {
                // The dialog hides the price row for these types; a programmatic
                // caller must not have the edit quietly dropped (trading safety:
                // never substitute, refuse instead).
                return OrderAmendResult.Failed(
                    _localization.GetString(AmendInvalidKey, $"{fresh.Type} orders cannot take a price edit"));
            }

            // Step 3 — build the amended request and intrinsically validate it
            // BEFORE any mutation: an unparsable replacement never gets to
            // cancel the original.
            var amended = BuildAmendedRequest(fresh, request);
            try
            {
                amended.Validate();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Amend: the amended request for {OrderId} is invalid and nothing was sent to the gates",
                    fresh.Id);
                return OrderAmendResult.Failed(_localization.GetString(AmendInvalidKey, ex.Message));
            }

            if (IsNoOpAmend(fresh, amended))
            {
                _logger.LogInformation(
                    "Amend: the edit of {OrderId} changes nothing; nothing was sent",
                    fresh.Id);
                return OrderAmendResult.Failed(_localization.GetString(AmendNoChangeKey));
            }

            if (amended.Quantity.Value < fresh.FilledQuantity.Value)
            {
                _logger.LogWarning(
                    "Amend: the amended quantity {NewQuantity} of {OrderId} is below its filled {FilledQuantity}; refused",
                    amended.Quantity.Value,
                    fresh.Id,
                    fresh.FilledQuantity.Value);

                // Numbers ride the message in the invariant culture, exactly
                // like the confirmation composer's own quantity arguments.
                return OrderAmendResult.Failed(
                    _localization.GetString(
                        AmendQtyBelowFilledKey,
                        amended.Quantity.Value.ToString(CultureInfo.InvariantCulture),
                        fresh.FilledQuantity.Value.ToString(CultureInfo.InvariantCulture)));
            }

            // Step 4 — the risk re-check of the WHOLE amended shape. A
            // rejection stops here and leaves the working order untouched.
            var risk = await _riskManager.CheckOrderAsync(amended, ct);
            if (!risk.IsApproved)
            {
                _logger.LogWarning(
                    "Amend: risk manager rejected the amended {OrderId}: {Reason}",
                    fresh.Id,
                    risk.Reason);
                return OrderAmendResult.Failed(_localization.GetString(AmendRejectedKey, risk.Reason ?? string.Empty));
            }

            // Step 5 — the confirmation rule: risk may demand one (its own
            // threshold), a fill-in-progress order always rewrites live
            // exposure, and the notional delta beyond the percent threshold
            // does too. A declined confirmation is a deliberate stop.
            var priceEdited = fresh.Type is OrderType.Limit or OrderType.StopLimit
                && amended.Price != fresh.Price;
            var requiresConfirmation = risk.RequiresConfirmation
                || fresh.FilledQuantity.Value > 0
                || IsLargeDelta(fresh, amended);
            if (requiresConfirmation)
            {
                var confirmed = await _confirmation.RequestConfirmationAsync(
                    _composer.ForAmend(fresh, amended, priceEdited),
                    ct);
                if (!confirmed)
                {
                    _logger.LogInformation(
                        "Amend: user declined the confirmation for {OrderId}; nothing placed or cancelled",
                        fresh.Id);
                    return OrderAmendResult.Silent();
                }
            }

            // Step 6 — the replace checkpoint A: the adapter is re-read at
            // replace time (after the user deliberated over the confirmation).
            var atReplace = await FindOpenOrderAsync(fresh.Id, ct);
            if (atReplace is null || !IsWorkingStatus(atReplace.Status))
            {
                _logger.LogInformation(
                    "Amend: order {OrderId} left the working set during the amend; the terminal state wins and no replacement is placed",
                    fresh.Id);
                return OrderAmendResult.Failed(_localization.GetString(AmendStaleKey, fresh.Id));
            }

            var replaced = _trading is IOrderReplaceSupport replace
                ? await ReplaceInPlaceAsync(replace, atReplace, amended, ct)
                : await CancelAndReplaceNewAsync(atReplace, amended, ct);

            return replaced;
        }
        catch (Exception ex)
        {
            // The last resort: the flow's own checkpoints convert the
            // expected failures; anything unexpected (a network fault, an
            // adapter crash) still lands as the localized error line instead
            // of escaping into the unobserved ReactiveCommand.ThrownExceptions.
            _logger.LogError(ex, "Amend: the amend of {OrderId} failed unexpectedly", working.Id);
            return OrderAmendResult.Failed(_localization.GetString(AmendFailedKey, ex.Message));
        }
    }

    #region Private Methods

    /// <summary>
    /// The adapter-side replace branch: supported adapters swap the order in
    /// place and own the race decision — a refused (terminal-state) replace
    /// surfaces as the localized stale message, nothing is cancelled
    /// separately by the flow.
    /// </summary>
    private async Task<OrderAmendResult> ReplaceInPlaceAsync(
        IOrderReplaceSupport replace,
        Order working,
        OrderRequest amended,
        CancellationToken ct)
    {
        Order replaced;
        try
        {
            replaced = await replace.ReplaceOrderAsync(working.Symbol, working.Id, amended, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The adapter refused: the order filled or ended while the amend
            // was in flight — the terminal state wins and is reported.
            _logger.LogInformation(
                ex,
                "Amend: the in-place replace of {OrderId} was refused (the order ended meanwhile); no replacement is placed",
                working.Id);
            return OrderAmendResult.Failed(_localization.GetString(AmendStaleKey, working.Id));
        }

        // An in-place replace keeps the order's identity: upsert its amended
        // shape into the persistent journal (best effort, never thrown into
        // the outcome) and mirror it into the session journal.
        await RecordReplacedAsync(replaced);
        _journal.RecordPlaced(replaced);

        _logger.LogInformation(
            "Amend: order {OrderId} amended in place by the adapter",
            replaced.Id);
        return OrderAmendResult.Done(_localization.GetString(AmendDoneInPlaceKey, replaced.Id));
    }

    /// <summary>
    /// The cancel+new branch for adapters without an in-place replace: the
    /// original is cancelled and the amended request is placed as a NEW
    /// order, and every step is surfaced as cancel+new. The cancel doubles
    /// as the final atomic state checkpoint on adapters like the paper one —
    /// an order that filled meanwhile refuses the cancel, the fill wins, and
    /// no replacement is placed (no double entry, no silent drop).
    /// </summary>
    private async Task<OrderAmendResult> CancelAndReplaceNewAsync(
        Order working,
        OrderRequest amended,
        CancellationToken ct)
    {
        try
        {
            // The adapter contract takes the symbol first and it must be the
            // order's own symbol — enforced here by construction.
            await _trading.CancelOrderAsync(working.Symbol, working.Id, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The paper race contract: a non-pending order refuses the
            // cancel, so the fill (or another terminal event) beat the amend.
            // The terminal state stands; nothing is re-placed alongside it.
            _logger.LogInformation(
                ex,
                "Amend: order {OrderId} filled or ended while the amend was in flight; the terminal state wins",
                working.Id);
            return OrderAmendResult.Failed(_localization.GetString(AmendStaleKey, working.Id));
        }
        catch (Exception ex)
        {
            // A cancel refused for any other reason (network faults ride the
            // same surface): the original is untouched, no half-way seam.
            _logger.LogError(ex, "Amend: cancelling {OrderId} failed", working.Id);
            return OrderAmendResult.Failed(_localization.GetString(AmendCancelFailedKey, ex.Message));
        }

        // The cancel landed — record it in the persistent history. Best
        // effort: the adapter has already cancelled the order, so a
        // persistence fault must never abort the flow.
        try
        {
            await _orderHistory.MarkCancelledAsync(
                working.ExchangeId,
                working.Id,
                DateTimeOffset.UtcNow,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Amend: failed to mark {OrderId} cancelled in the order history",
                working.Id);
        }

        try
        {
            var replaced = await _trading.PlaceOrderAsync(amended, ct);
            _journal.RecordPlaced(replaced);

            // Best effort as above: the adapter accepted the replacement
            // already; persistence must not turn the outcome into an error.
            await RecordReplacedAsync(replaced);

            _logger.LogInformation(
                "Amend: order {OldOrderId} cancelled and replaced by {NewOrderId} (cancel+new)",
                working.Id,
                replaced.Id);
            return OrderAmendResult.Done(
                _localization.GetString(AmendDoneKey, working.Id, replaced.Id));
        }
        catch (Exception ex)
        {
            // The original is gone and the replacement was refused: this is
            // reported loudly — the panel never shows a working order that
            // does not exist, and never hides the hole either (no silent drop).
            _logger.LogError(
                ex,
                "Amend: order {OrderId} was cancelled but the amended replacement failed",
                working.Id);
            return OrderAmendResult.Failed(
                _localization.GetString(AmendReplaceFailedKey, working.Id, ex.Message));
        }
    }

    /// <summary>Best-effort upsert of a replaced order's shape into the persistent history.</summary>
    private async Task RecordReplacedAsync(Order replaced)
    {
        try
        {
            await _orderHistory.RecordPlacedAsync(replaced);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Amend: failed to persist {OrderId} to the order history",
                replaced.Id);
        }
    }

    /// <summary>Find the order's current open state in the adapter's open set; null when it left it.</summary>
    private async Task<Order?> FindOpenOrderAsync(string orderId, CancellationToken ct)
    {
        var open = await _trading.GetOpenOrdersAsync(ct);
        return open.FirstOrDefault(order => order.Id == orderId);
    }

    /// <summary>True while the status is still an active one a cancel/replace can act on.</summary>
    private static bool IsWorkingStatus(OrderStatus status) =>
        status is OrderStatus.Pending or OrderStatus.Submitted or OrderStatus.PartiallyFilled;

    /// <summary>Build the amended request from the FRESH order state — the display row is never trusted.</summary>
    private static OrderRequest BuildAmendedRequest(Order fresh, OrderAmendRequest edits) => new()
    {
        Symbol = fresh.Symbol,
        Side = fresh.Side,
        Type = fresh.Type,
        Quantity = new Quantity(edits.NewQuantity),

        // Only the limit-price-bearing types take a price edit; a trailing
        // stop's display price is its ratcheting trigger, not an editable
        // level — the request re-places with Price left unset, exactly like
        // the placement of the original did.
        Price = fresh.Type is OrderType.Limit or OrderType.StopLimit
            ? edits.NewLimitPrice is { } edited ? new Price(edited) : fresh.Price
            : null,
        StopPrice = fresh.StopPrice,
        TrailDistance = fresh.TrailDistance,
        OcoGroupId = fresh.OcoGroupId,
        TimeInForce = fresh.TimeInForce,
        Expiry = fresh.Expiry,
        ClientOrderId = fresh.ClientOrderId,
    };

    /// <summary>
    /// True when the edit would neither change the quantity nor the editable
    /// limit price: a no-op amend through cancel+new is real risk (a cancel
    /// or fill can race it) for zero effect, so it is refused.
    /// </summary>
    private static bool IsNoOpAmend(Order fresh, OrderRequest amended)
    {
        if (amended.Quantity.Value != fresh.Quantity.Value)
        {
            return false;
        }

        return !IsPriceEditableType(fresh.Type) || amended.Price == fresh.Price;
    }

    /// <summary>True for the types whose working order displays an editable limit price.</summary>
    private static bool IsPriceEditableType(OrderType type) =>
        type is OrderType.Limit or OrderType.StopLimit;

    /// <summary>
    /// The notional anchor of a request, the same rule the risk manager
    /// estimates by: the limit price for limit-bearing orders, the stop
    /// trigger for stop-family orders. Comparing originals and amendments on
    /// the same anchor keeps the delta honest.
    /// </summary>
    private static Price? NotionalAnchor(OrderRequest request) =>
        request.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.TrailingStop
            ? request.StopPrice ?? request.Price
            : request.Price;

    /// <summary>
    /// The notional-delta leg of the confirmation rule: relative notional
    /// change beyond <see cref="LargeAmendDeltaPercent"/>, computed on the
    /// anchor prices of the original and the amended shapes.
    /// </summary>
    private static bool IsLargeDelta(Order fresh, OrderRequest amended)
    {
        var originalAnchor = NotionalAnchor(BuildOriginalRequest(fresh));
        var amendedAnchor = NotionalAnchor(amended);
        if (originalAnchor is not { } originalPrice || amendedAnchor is not { } amendedPrice)
        {
            // No anchor means no measurable notional; the delta rule cannot
            // fire and the other legs of the rule (risk confirmation, fill in
            // progress) still apply.
            return false;
        }

        var original = fresh.Quantity.Value * originalPrice.Value;
        var amendedNotional = amended.Quantity.Value * amendedPrice.Value;
        if (original <= 0)
        {
            return false;
        }

        var deltaPercent = Math.Abs(amendedNotional - original) / original * 100m;
        return deltaPercent > LargeAmendDeltaPercent;
    }

    /// <summary>
    /// The original shape of a working order as a request, so the delta rule
    /// runs BOTH sides through the same anchoring helper.
    /// </summary>
    private static OrderRequest BuildOriginalRequest(Order fresh) => new()
    {
        Symbol = fresh.Symbol,
        Side = fresh.Side,
        Type = fresh.Type,
        Quantity = fresh.Quantity,
        Price = fresh.Price,
        StopPrice = fresh.StopPrice,
        TrailDistance = fresh.TrailDistance,
        Expiry = fresh.Expiry,
        TimeInForce = fresh.TimeInForce,
    };

    #endregion Private Methods
}
