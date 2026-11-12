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
/// Protective SL/TP levels for the positions panel (T-187): the position's
/// stop-loss and take-profit edit rides the EXISTING conditional machinery —
/// the protective pair is an either-or group of reduce-only legs placed
/// through <see cref="ITradingAdapter.PlaceOrderAsync"/> and torn down
/// through <see cref="ITradingAdapter.CancelOrderAsync"/>. No Core contract
/// changes: the levels the venue reports ride back through the position
/// itself, so the panel and MCP's <c>get_positions</c> read them the same
/// way.
/// <para>
/// The mandatory composition path holds for every attempt: the position is
/// re-read fresh (never the stale panel row), the levels are direction-checked
/// against the CURRENT market price (a level on the wrong side would either
/// fire at once or never fire — both are refusals, never defaults), each leg
/// is intrinsically validated and risk-checked BEFORE any mutation, and ONE
/// confirmation gates the whole rewrite. The rewrite semantics are cancel
/// then place: the working protective legs the adapter still serves cancel
/// first (best-effort against already-terminal legs), then the new pair
/// places take-profit first, stop-loss second — a refused stop-loss cancels
/// the placed take-profit and reports honestly, so a half-protection never
/// stays working silently. A clear request cancels only. The cancel-then-place
/// sequence leaves a brief unprotected gap between its halves; the confirmation
/// names the rewrite, and the abort path reports the unprotected state loudly —
/// the panel never lies about what reached the surface.
/// </para>
/// </summary>
public sealed class ProtectiveLevelsService
{
    #region Resx Keys

    private const string NoPositionKey = "Positions.NoPositionFormat";
    private const string RacePositionChangedKey = "Positions.RacePositionChangedFormat";
    private const string ActionRejectedKey = "Positions.ActionRejectedFormat";
    private const string ActionFailedKey = "Positions.ActionFailedFormat";
    private const string ProtectNothingToClearKey = "Positions.ProtectNothingToClear";
    private const string ProtectLevelClearedKey = "Positions.ProtectLevelCleared";
    private const string ProtectStopBelowMarketKey = "Positions.ProtectStopMustBeBelowMarket";
    private const string ProtectStopAboveMarketKey = "Positions.ProtectStopMustBeAboveMarket";
    private const string ProtectTakeProfitAboveMarketKey = "Positions.ProtectTakeProfitMustBeAboveMarket";
    private const string ProtectTakeProfitBelowMarketKey = "Positions.ProtectTakeProfitMustBeBelowMarket";
    private const string ProtectSetFormatKey = "Positions.ProtectSetFormat";
    private const string ProtectAbortedFormatKey = "Positions.ProtectAbortedFormat";
    private const string ProtectLevelNoneKey = "Positions.ProtectLevelNone";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<ProtectiveLevelsService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the protective levels service over the trading face and the
    /// mandatory order pipeline's gates.
    /// </summary>
    public ProtectiveLevelsService(
        ITradingAdapter trading,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<ProtectiveLevelsService> logger)
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

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Apply the protective levels to the position the panel row rendered
    /// (T-187): a set rewrites the working protective pair (cancel then
    /// place), a clear cancels it, both ride ONE confirmation and the risk
    /// gates before any mutation. <paramref name="stopLoss"/> and
    /// <paramref name="takeProfit"/> are the parsed dialog levels; a null
    /// level means "clear that level", both nulls mean "clear the pair".
    /// </summary>
    public Task<PositionActionResult> ProtectAsync(
        Position position,
        decimal? stopLoss,
        decimal? takeProfit,
        CancellationToken ct = default)
        => RunAttemptAsync(async () =>
        {
            // The fresh state is authoritative: the row the user clicked can
            // be stale by seconds or minutes, and the rewrite must never size
            // against it (the same guard every position action holds).
            var fresh = await FindOpenPositionAsync(position.Symbol, ct);
            if (fresh is null)
            {
                _logger.LogInformation(
                    "Protective levels: no open position on {Symbol}; the action is rejected",
                    position.Symbol.Normalized);
                return FailWith(NoPositionKey, position.Symbol.Normalized);
            }

            if (fresh.Side != position.Side || fresh.Quantity.Value != position.Quantity.Value)
            {
                _logger.LogInformation(
                    "Protective levels: {Symbol} changed from {ConfirmedQuantity} to {FreshQuantity} before the rewrite; nothing placed",
                    position.Symbol.Normalized,
                    position.Quantity.Value,
                    fresh.Quantity.Value);
                return FailWith(
                    RacePositionChangedKey,
                    _localization.GetString($"Order.{fresh.Side}"),
                    FormatQuantity(fresh.Quantity.Value),
                    fresh.Symbol.Normalized);
            }

            // Direction validation against the CURRENT market price: a
            // stop-loss on the wrong side fires at once (or never arms), a
            // take-profit on the wrong side fills at placement — both are
            // explicit localized rejections, never a silent reinterpretation.
            var directionError = FindDirectionError(fresh, stopLoss, takeProfit);
            if (directionError is not null)
            {
                _logger.LogInformation(
                    "Protective levels: a level sits on the wrong side of the market on {Symbol}; the action is rejected",
                    fresh.Symbol.Normalized);
                return PositionActionResult.Fail(directionError);
            }

            var exitSide = ExitSideOf(fresh);
            var existing = await FindProtectiveLegsAsync(fresh.Symbol, exitSide, ct);

            if (stopLoss is null && takeProfit is null)
            {
                if (existing.Count == 0)
                {
                    _logger.LogInformation(
                        "Protective levels: nothing to clear on {Symbol}; no protective legs are working",
                        fresh.Symbol.Normalized);
                    return PositionActionResult.PlacedOk(
                        [],
                        _localization.GetString(ProtectNothingToClearKey, fresh.Symbol.Normalized));
                }

                return await ClearAsync(fresh, exitSide, existing, ct);
            }

            return await RewriteAsync(fresh, exitSide, existing, stopLoss, takeProfit, ct);
        }, ct);

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The clear path: cancel the working protective legs, place nothing.
    /// One confirmation gates the cancellation (removing a stop is as
    /// risk-relevant as placing one), the cancels are best-effort against
    /// already-terminal legs, and the either-or teardown takes the sibling
    /// of each cancelled leg down with it.
    /// </summary>
    private async Task<PositionActionResult> ClearAsync(
        Position fresh,
        OrderSide exitSide,
        IReadOnlyList<Order> existing,
        CancellationToken ct)
    {
        if (!await ConfirmAsync(fresh, null, null, ct))
        {
            return PositionActionResult.Declined();
        }

        // The pre-mutation freshness guard: the user deliberated over the
        // confirmation, and the position may have closed or changed meanwhile
        // — a vanished position has nothing to protect and nothing to clear
        // (the working legs, if any, self-clean on their own triggers).
        var freshnessRejection = await FindFreshnessRejectionAsync(fresh, ct);
        if (freshnessRejection is not null)
        {
            return PositionActionResult.Fail(freshnessRejection);
        }

        var cancelError = await CancelProtectiveLegsAsync(existing, ct);
        if (cancelError is not null)
        {
            // A cancel refused for a real reason (a network fault rides the
            // same surface): the attempt aborts before anything else moves,
            // and whatever protection survived stays working — reported.
            return PositionActionResult.Fail(cancelError);
        }

        _logger.LogInformation(
            "Protective levels: cleared {LegCount} protective leg(s) on {Symbol}",
            existing.Count,
            fresh.Symbol.Normalized);
        return PositionActionResult.PlacedOk(
            [],
            _localization.GetString(ProtectLevelClearedKey, fresh.Symbol.Normalized));
    }

    /// <summary>
    /// The set/rewrite path: every gate BEFORE any mutation — the new legs
    /// are intrinsically validated and risk-checked as a pair (both legs or
    /// nothing reaches the confirmation), ONE confirmation names the rewrite,
    /// then the working protective legs cancel and the new pair places
    /// take-profit first, stop-loss second.
    /// </summary>
    private async Task<PositionActionResult> RewriteAsync(
        Position fresh,
        OrderSide exitSide,
        IReadOnlyList<Order> existing,
        decimal? stopLoss,
        decimal? takeProfit,
        CancellationToken ct)
    {
        var groupId = $"oco-{Guid.NewGuid().ToString("N")}";
        var takeProfitRequest = takeProfit is { } takeProfitLevel
            ? TakeProfitLeg(fresh, exitSide, takeProfitLevel, groupId)
            : null;
        var stopLossRequest = stopLoss is { } stopLossLevel
            ? StopLossLeg(fresh, exitSide, stopLossLevel, groupId)
            : null;

        // The intrinsic matrix first: an unparsable leg never reaches a gate,
        // let alone the adapter.
        var validationError = FindValidationError(takeProfitRequest) ?? FindValidationError(stopLossRequest);
        if (validationError is not null)
        {
            return PositionActionResult.Fail(validationError);
        }

        // The risk pair is ATOMIC: both legs are checked before anything is
        // cancelled or placed — a rejection leaves the existing pair intact.
        var takeProfitRejection = await FindRiskRejectionAsync(takeProfitRequest, ct);
        if (takeProfitRejection is not null)
        {
            return PositionActionResult.Fail(takeProfitRejection);
        }

        var stopLossRejection = await FindRiskRejectionAsync(stopLossRequest, ct);
        if (stopLossRejection is not null)
        {
            return PositionActionResult.Fail(stopLossRejection);
        }

        if (!await ConfirmAsync(fresh, stopLoss, takeProfit, ct))
        {
            return PositionActionResult.Declined();
        }

        // The pre-mutation freshness guard (the same one every confirmed
        // exit holds): the legs are sized by the confirmation's position, so
        // a position that vanished or changed while the user deliberated
        // refuses the rewrite instead of executing stale sizing.
        var freshnessRejection = await FindFreshnessRejectionAsync(fresh, ct);
        if (freshnessRejection is not null)
        {
            return PositionActionResult.Fail(freshnessRejection);
        }

        // The rewrite's first half: the old pair leaves the surface. A real
        // cancel failure aborts the whole attempt BEFORE the new legs place —
        // a half-cancelled old pair must never meet a fresh one.
        var cancelError = await CancelProtectiveLegsAsync(existing, ct);
        if (cancelError is not null)
        {
            return PositionActionResult.Fail(cancelError);
        }

        // The new pair: take-profit first (the ticket's pair order), then the
        // stop-loss. A refused stop-loss cancels the working take-profit and
        // reports loudly — a half-protected position is never left silently.
        var placed = new List<Order>();
        try
        {
            if (takeProfitRequest is not null)
            {
                placed.Add(await PlaceOrderAsync(takeProfitRequest, ct));
            }

            if (stopLossRequest is not null)
            {
                placed.Add(await PlaceOrderAsync(stopLossRequest, ct));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Protective levels: the second leg was refused on {Symbol}; the placed leg cancels with it",
                fresh.Symbol.Normalized);

            if (placed is [var placedLeg])
            {
                await CancelLegAsync(placedLeg, ct);
                return PositionActionResult.Fail(
                    _localization.GetString(
                        ProtectAbortedFormatKey,
                        placedLeg.Id,
                        ex.Message),
                    [placedLeg]);
            }

            return PositionActionResult.Fail(_localization.GetString(ActionFailedKey, ex.Message));
        }

        _logger.LogInformation(
            "Protective levels: {Symbol} protected by {OrderCount} order(s) ({OcoGroup})",
            fresh.Symbol.Normalized,
            placed.Count,
            groupId);
        return PositionActionResult.PlacedOk(
            placed,
            _localization.GetString(
                ProtectSetFormatKey,
                fresh.Symbol.Normalized,
                LevelText(stopLoss),
                LevelText(takeProfit)));
    }

    /// <summary>
    /// The direction matrix: for a long the stop-loss must sit below and the
    /// take-profit above the current price (strictly — a level AT the market
    /// either refuses to arm or fills at placement); for a short mirrored.
    /// An undefined side never derives a rule — it is an explicit refusal.
    /// </summary>
    private string? FindDirectionError(
        Position position,
        decimal? stopLoss,
        decimal? takeProfit)
    {
        var current = position.CurrentPrice.Value;
        var currentText = current.ToString("F8", CultureInfo.InvariantCulture);

        // An undefined side derives no direction rule here — it falls
        // through to the exit-side derivation, whose honest refusal names
        // the unsupported side (never a defaulted direction message).
        if (stopLoss is { } stop && position.Side is OrderSide.Buy or OrderSide.Sell)
        {
            var stopIsWrong = position.Side == OrderSide.Buy
                ? stop >= current
                : stop <= current;

            if (stopIsWrong)
            {
                return _localization.GetString(
                    position.Side == OrderSide.Buy
                        ? ProtectStopBelowMarketKey
                        : ProtectStopAboveMarketKey,
                    currentText);
            }
        }

        if (takeProfit is { } takeProfitLevel && position.Side is OrderSide.Buy or OrderSide.Sell)
        {
            var takeProfitIsWrong = position.Side == OrderSide.Buy
                ? takeProfitLevel <= current
                : takeProfitLevel >= current;

            if (takeProfitIsWrong)
            {
                return _localization.GetString(
                    position.Side == OrderSide.Buy
                        ? ProtectTakeProfitAboveMarketKey
                        : ProtectTakeProfitBelowMarketKey,
                    currentText);
            }
        }

        return null;
    }

    /// <summary>
    /// The working protective legs the adapter still serves on the symbol:
    /// reduce-only opposite-side orders under an OCO group id — exactly the
    /// shape the edit's pair (and the bracket's exits) place. Cancel-then-place
    /// rewrites them so levels never stack.
    /// </summary>
    private async Task<IReadOnlyList<Order>> FindProtectiveLegsAsync(
        Symbol symbol,
        OrderSide exitSide,
        CancellationToken ct)
    {
        var open = await _trading.GetOpenOrdersAsync(ct);
        return open
            .Where(order =>
                order.Symbol.Normalized == symbol.Normalized
                && order.ReduceOnly
                && order.Side == exitSide
                && order.OcoGroupId is not null)
            .ToArray();
    }

    /// <summary>
    /// Cancel the working protective legs (symbol-first, each by its own id):
    /// an already-terminal leg refuses the cancel with the honest reason and
    /// is skipped (its either-or sibling is gone with it); any other refusal
    /// aborts the attempt — the panel reports it and whatever protection
    /// survived stays working.
    /// </summary>
    private async Task<string?> CancelProtectiveLegsAsync(
        IReadOnlyList<Order> legs,
        CancellationToken ct)
    {
        foreach (var leg in legs)
        {
            try
            {
                await _trading.CancelOrderAsync(leg.Symbol, leg.Id, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Already terminal (filled or cancelled a moment ago): the
                // best-effort skip is honest — the leg is gone either way.
                _logger.LogInformation(
                    ex,
                    "Protective levels: the leg {OrderId} was already terminal; skipped",
                    leg.Id);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Protective levels: cancelling {OrderId} failed", leg.Id);
                return _localization.GetString(ActionFailedKey, ex.Message);
            }

            // The adapter cancelled the leg — mirror it into the persistent
            // history. Best effort: a persistence fault must never abort the
            // rewrite (the same contract the panel's own cancel holds).
            try
            {
                await _orderHistory.MarkCancelledAsync(leg.ExchangeId, leg.Id, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Protective levels: failed to mark {OrderId} cancelled in the order history",
                    leg.Id);
            }
        }

        return null;
    }

    /// <summary>One leg cancel that reports whether the cancel landed (the
    /// abort path's message names an unprotected position only when it did).</summary>
    private async Task<bool> CancelLegAsync(Order leg, CancellationToken ct)
    {
        try
        {
            await _trading.CancelOrderAsync(leg.Symbol, leg.Id, ct);
            try
            {
                await _orderHistory.MarkCancelledAsync(leg.ExchangeId, leg.Id, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Protective levels: failed to mark {OrderId} cancelled in the order history",
                    leg.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Protective levels: the abort cancel of {OrderId} failed",
                leg.Id);
            return false;
        }
    }

    /// <summary>
    /// The one mandatory confirmation over the whole rewrite (a position
    /// change is high-risk; a protective rewrite changes its risk profile
    /// both ways). A refusal leaves nothing on the surface.
    /// </summary>
    private async Task<bool> ConfirmAsync(
        Position fresh,
        decimal? stopLoss,
        decimal? takeProfit,
        CancellationToken ct)
    {
        if (await _confirmation.RequestConfirmationAsync(_composer.ForProtect(fresh, stopLoss, takeProfit), ct))
        {
            return true;
        }

        _logger.LogInformation(
            "Protective levels: the user refused the confirmation on {Symbol}; nothing placed or cancelled",
            fresh.Symbol.Normalized);
        return false;
    }

    /// <summary>
    /// The take-profit leg: a reduce-only limit above the market (the level
    /// itself is the fill price), riding the fresh either-or group.
    /// </summary>
    private static OrderRequest TakeProfitLeg(
        Position position,
        OrderSide exitSide,
        decimal level,
        string groupId) => new()
        {
            Symbol = position.Symbol,
            Side = exitSide,
            Type = OrderType.Limit,
            Quantity = position.Quantity,
            Price = new Price(level),
            OcoGroupId = groupId,
            ReduceOnly = true,
        };

    /// <summary>
    /// The stop-loss leg: a reduce-only stop-limit whose limit equals its
    /// trigger — the stop-market reading of the protective stop (the paper
    /// fill executes at the level; live adapters shape-map the same request).
    /// </summary>
    private static OrderRequest StopLossLeg(
        Position position,
        OrderSide exitSide,
        decimal level,
        string groupId) => new()
        {
            Symbol = position.Symbol,
            Side = exitSide,
            Type = OrderType.StopLimit,
            Quantity = position.Quantity,
            Price = new Price(level),
            StopPrice = new Price(level),
            OcoGroupId = groupId,
            ReduceOnly = true,
        };

    /// <summary>
    /// The body of the attempt, wrapped so any fault — a network exception,
    /// an adapter refusing an unsupported shape, or the flow's own
    /// cancellation while the panel closes — resolves into the localized
    /// failure line instead of escaping into an unobserved command exception.
    /// </summary>
    private async Task<PositionActionResult> RunAttemptAsync(
        Func<Task<PositionActionResult>> attempt,
        CancellationToken ct)
    {
        try
        {
            return await attempt();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                _logger.LogInformation(ex, "Protective levels: the action was cancelled before completing");
            }
            else
            {
                _logger.LogError(ex, "Protective levels: the action failed before completing");
            }

            return PositionActionResult.Fail(_localization.GetString(ActionFailedKey, ex.Message));
        }
    }

    /// <summary>The open position on a symbol as the adapter serves it RIGHT NOW: a zero or missing position reads as none.</summary>
    private async Task<Position?> FindOpenPositionAsync(
        Symbol symbol,
        CancellationToken ct)
    {
        var positions = await _trading.GetPositionsAsync(ct);
        return positions.FirstOrDefault(candidate =>
            candidate.Symbol.Normalized == symbol.Normalized
            && candidate.Quantity.Value > decimal.Zero);
    }

    /// <summary>
    /// The pre-mutation freshness guard of a confirmed rewrite: the position
    /// must still exist with exactly the side and quantity the confirmation
    /// named. A fill that landed first wins and is reported — the vanished
    /// position is the explicit rejection, a changed one is the race report;
    /// both leave nothing on the surface.
    /// </summary>
    private async Task<string?> FindFreshnessRejectionAsync(
        Position confirmed,
        CancellationToken ct)
    {
        var fresh = await FindOpenPositionAsync(confirmed.Symbol, ct);
        if (fresh is null)
        {
            _logger.LogInformation(
                "Protective levels: {Symbol} has no open position left before the rewrite; nothing placed or cancelled",
                confirmed.Symbol.Normalized);
            return _localization.GetString(NoPositionKey, confirmed.Symbol.Normalized);
        }

        if (fresh.Side != confirmed.Side || fresh.Quantity.Value != confirmed.Quantity.Value)
        {
            _logger.LogInformation(
                "Protective levels: {Symbol} changed from {ConfirmedQuantity} to {FreshQuantity} before the rewrite; nothing placed or cancelled",
                confirmed.Symbol.Normalized,
                confirmed.Quantity.Value,
                fresh.Quantity.Value);
            return _localization.GetString(
                RacePositionChangedKey,
                _localization.GetString($"Order.{fresh.Side}"),
                FormatQuantity(fresh.Quantity.Value),
                fresh.Symbol.Normalized);
        }

        return null;
    }

    /// <summary>
    /// The side that exits a position: an undefined side is never defaulted
    /// into an entry (trading-safety rule) — it is an explicit refusal.
    /// </summary>
    private static OrderSide ExitSideOf(Position position)
    {
        if (position.Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new InvalidOperationException(
                $"Unsupported position side {position.Side}; the protective side cannot be derived for {position.Symbol.Normalized}.");
        }

        return position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
    }

    /// <summary>The intrinsic request matrix: an unparsable request never reaches any gate.</summary>
    private string? FindValidationError(OrderRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        try
        {
            request.Validate();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Protective levels: the built order request is invalid and was never sent to a gate");
            return _localization.GetString(ActionFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// The risk gate: an unapproved leg is a localized rejection and never
    /// reaches a confirmation dialog or an adapter — exactly like the
    /// new-order dialog's pipeline.
    /// </summary>
    private async Task<string?> FindRiskRejectionAsync(
        OrderRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return null;
        }

        var risk = await _riskManager.CheckOrderAsync(request, ct);
        if (risk.IsApproved)
        {
            return null;
        }

        _logger.LogWarning(
            "Protective levels: risk controls rejected the {Side} {Type} {Quantity} {Symbol} leg: {Reason}",
            request.Side,
            request.Type,
            request.Quantity.Value,
            request.Symbol.Normalized,
            risk.Reason);
        return _localization.GetString(ActionRejectedKey, risk.Reason ?? string.Empty);
    }

    /// <summary>
    /// Place one order and record it in the session journal and the SQLite
    /// order history. The persistence half is best effort: the adapter has
    /// already accepted the order, so a record fault must never turn the
    /// placement into a failure (the same contract the dialog holds).
    /// </summary>
    private async Task<Order> PlaceOrderAsync(
        OrderRequest request,
        CancellationToken ct)
    {
        var order = await _trading.PlaceOrderAsync(request, ct);
        _journal.RecordPlaced(order);

        try
        {
            await _orderHistory.RecordPlacedAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Protective levels: failed to persist {OrderId} to the order history",
                order.Id);
        }

        return order;
    }

    /// <summary>A failed attempt carrying a template key filled with arguments.</summary>
    private PositionActionResult FailWith(
        string messageKey,
        params object[] arguments) =>
        PositionActionResult.Fail(_localization.GetString(messageKey, arguments));

    /// <summary>The F8 text of a level, or the localized "not set" for a cleared one.</summary>
    private string LevelText(decimal? level) => level is { } value
        ? value.ToString("F8", CultureInfo.InvariantCulture)
        : _localization.GetString(ProtectLevelNoneKey);

    /// <summary>Format a quantity with the invariant culture for messages.</summary>
    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString(CultureInfo.InvariantCulture);

    #endregion Private Methods
}
