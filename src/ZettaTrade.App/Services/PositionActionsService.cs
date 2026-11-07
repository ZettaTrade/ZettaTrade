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
/// Position actions for the positions panel: close (flatten at market),
/// partial close by an explicit amount, and reverse (close, then re-open
/// the opposite side) — every order sized against the CURRENT position,
/// never against a stale panel row. Each attempt rides the mandatory
/// composition path the new-order dialog uses: <c>OrderRequest.Validate</c>
/// → <see cref="IRiskManager"/> (a rejection never reaches a confirmation)
/// → <see cref="IConfirmationService"/> → <see cref="ITradingAdapter"/>,
/// and the reverse asks for ONE confirmation over both legs before either
/// is placed — a refusal leaves nothing on the surface.
/// The reverse is exactly two validated orders: the exit leg (reduce-only)
/// and the re-entry leg (plain market). Position state is re-read before
/// every placement, so an external fill that lands first wins and is
/// reported — no stale-sized order, no double entry, no silent drop; a
/// reverse whose exit did not leave the symbol flat aborts its re-entry
/// with an honest localized report instead of a half-reverse running on.
/// Demo/paper adapters fill market legs against the freshest reference
/// close the <see cref="MarketReference"/> seam resolves (the same price
/// the shipped <c>DefaultRiskManager</c> anchors the notional on); live
/// adapters shape-map the same validated request — no new capability.
/// </summary>
public sealed class PositionActionsService
{
    #region Resx Keys

    private const string ClosedKey = "Positions.ClosedFormat";
    private const string ReducedKey = "Positions.ReducedFormat";
    private const string ReversedKey = "Positions.ReversedFormat";
    private const string NoPositionKey = "Positions.NoPositionFormat";
    private const string ReduceNotANumberKey = "Positions.ReduceNotANumberFormat";
    private const string ReduceTooLargeKey = "Positions.ReduceTooLargeFormat";
    private const string ActionRejectedKey = "Positions.ActionRejectedFormat";
    private const string ActionFailedKey = "Positions.ActionFailedFormat";
    private const string RacePositionChangedKey = "Positions.RacePositionChangedFormat";
    private const string ReverseAbortedKey = "Positions.ReverseAbortedFormat";
    private const string ReverseEntryFailedKey = "Positions.ReverseLeg2FailedFormat";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<PositionActionsService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the position actions service over the trading face and the
    /// mandatory order pipeline's gates.
    /// </summary>
    public PositionActionsService(
        ITradingAdapter trading,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<PositionActionsService> logger)
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
    /// Flatten the position on <paramref name="symbol"/> with one reduce-only
    /// market order sized against the position AS IT IS when the order is
    /// placed (missing or zero quantity is an explicit rejection — never a
    /// degenerate order), validating, risk-checking and confirming exactly
    /// like a hand-built placement.
    /// </summary>
    public Task<PositionActionResult> CloseAsync(
        Symbol symbol,
        CancellationToken ct = default)
        => RunActionAsync(async () =>
        {
            var position = await FindOpenPositionAsync(symbol, ct);
            if (position is null)
            {
                _logger.LogInformation(
                    "Position actions: close has no open position on {Symbol}; the action is rejected",
                    symbol.Normalized);
                return FailWith(NoPositionKey, symbol.Normalized);
            }

            return await RunConfirmedExitAsync(
                position,
                position.Quantity.Value,
                ClosedKey,
                ct);
        }, ct);

    /// <summary>
    /// Reduce the position on <paramref name="symbol"/> by <paramref name="rawQuantity"/>
    /// (a strictly positive invariant-culture amount) with one reduce-only
    /// market order. The amount must be positive and must fit the open
    /// position as it is when the order is placed — a zero, negative,
    /// unparsable or oversized amount is an explicit localized rejection.
    /// </summary>
    public Task<PositionActionResult> ReduceAsync(
        Symbol symbol,
        string rawQuantity,
        CancellationToken ct = default)
        => RunActionAsync(async () =>
        {
            if (!TryParsePositiveDecimal(rawQuantity, out var reduceBy))
            {
                var raw = (rawQuantity ?? string.Empty).Trim();
                _logger.LogInformation(
                    "Position actions: unparsable reduce amount '{Raw}'", raw);
                return FailWith(ReduceNotANumberKey, raw);
            }

            var position = await FindOpenPositionAsync(symbol, ct);
            if (position is null)
            {
                _logger.LogInformation(
                    "Position actions: reduce has no open position on {Symbol}; the action is rejected",
                    symbol.Normalized);
                return FailWith(NoPositionKey, symbol.Normalized);
            }

            if (reduceBy > position.Quantity.Value)
            {
                // The partial close must fit inside the open position as it
                // stands — never a clamp, the explicit rejection is honest.
                _logger.LogInformation(
                    "Position actions: reduce amount {Requested} exceeds the open position {Open} on {Symbol}",
                    reduceBy,
                    position.Quantity.Value,
                    symbol.Normalized);
                return FailWith(
                    ReduceTooLargeKey,
                    FormatQuantity(reduceBy),
                    FormatQuantity(position.Quantity.Value));
            }

            return await RunConfirmedExitAsync(
                position,
                reduceBy,
                ReducedKey,
                ct);
        }, ct);

    /// <summary>
    /// Reverse the position on <paramref name="symbol"/>: exactly two
    /// validated, risk-checked orders — first a reduce-only market close of
    /// the whole position, then a same-sized market entry on the opposite
    /// side — announced by ONE confirmation over both legs (canon: a position
    /// change is high-risk). A refusal or a failed gate leaves nothing
    /// placed; after the exit lands the symbol must be flat before the
    /// re-entry, and an external fill that won instead is reported with the
    /// exit named — the flow aborts honestly, never a half-reverse running
    /// on silently.
    /// </summary>
    public Task<PositionActionResult> ReverseAsync(
        Symbol symbol,
        CancellationToken ct = default)
        => RunActionAsync(async () =>
        {
            var position = await FindOpenPositionAsync(symbol, ct);
            if (position is null)
            {
                _logger.LogInformation(
                    "Position actions: reverse has no open position on {Symbol}; the action is rejected",
                    symbol.Normalized);
                return FailWith(NoPositionKey, symbol.Normalized);
            }

            var reference = await MarketReference.GetLatestCloseAsync(_trading, position.Symbol, ct);
            var exitRequest = MarketRequest(
                position.Symbol,
                ExitSideOf(position),
                position.Quantity.Value,
                reduceOnly: true,
                reference);
            var entryRequest = MarketRequest(
                position.Symbol,
                ExitSideOf(position),
                position.Quantity.Value,
                reduceOnly: false,
                reference);

            var validationError = FindValidationError(exitRequest) ?? FindValidationError(entryRequest);
            if (validationError is not null)
            {
                return PositionActionResult.Fail(validationError);
            }

            var exitRejection = await FindRiskRejectionAsync(exitRequest, ct);
            if (exitRejection is not null)
            {
                return PositionActionResult.Fail(exitRejection);
            }

            var entryRejection = await FindRiskRejectionAsync(entryRequest, ct);
            if (entryRejection is not null)
            {
                return PositionActionResult.Fail(entryRejection);
            }

            if (!await _confirmation.RequestConfirmationAsync(_composer.ForReverse(exitRequest, entryRequest), ct))
            {
                _logger.LogInformation(
                    "Position actions: the user refused the reverse confirmation; nothing placed");
                return PositionActionResult.Declined();
            }

            var fresh = await FindOpenPositionAsync(symbol, ct);
            var freshnessRejection = FindFreshnessRejection(position, fresh);
            if (freshnessRejection is not null)
            {
                return PositionActionResult.Fail(freshnessRejection);
            }

            var exit = await PlaceOrderAsync(exitRequest, ct);

            var postExit = await FindOpenPositionAsync(symbol, ct);
            if (postExit is not null)
            {
                // The exit did not leave the symbol flat (a fill landed
                // first, or the exit only partially filled upstream): the
                // re-entry would double against an existing position — abort
                // it honestly with the exit order named.
                _logger.LogInformation(
                    "Position actions: the reverse aborted after the exit {ExitId}; a position still exists on {Symbol}",
                    exit.Id,
                    symbol.Normalized);
                return new PositionActionResult
                {
                    Outcome = PositionActionOutcome.Failed,
                    PlacedOrders = [exit],
                    ErrorMessage = _localization.GetString(
                        ReverseAbortedKey,
                        symbol.Normalized,
                        _localization.GetString($"Order.{postExit.Side}"),
                        FormatQuantity(postExit.Quantity.Value)),
                };
            }

            try
            {
                var entry = await PlaceOrderAsync(entryRequest, ct);
                return PositionActionResult.PlacedOk(
                    [exit, entry],
                    _localization.GetString(ReversedKey, exit.Id, entry.Id));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Position actions: the re-entry failed after the exit {ExitId} was placed",
                    exit.Id);
                return new PositionActionResult
                {
                    Outcome = PositionActionOutcome.Failed,
                    PlacedOrders = [exit],
                    ErrorMessage = _localization.GetString(
                        ReverseEntryFailedKey,
                        exit.Id,
                        ex.Message),
                };
            }
        }, ct);

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Validate, risk-check, confirm, freshness-guard and place ONE reduce-only
    /// market exit, then report it. Sized by the caller from a position freshly
    /// read off the adapter; the run re-reads the position once more right
    /// before the placement, so a fill that landed after the confirmation and
    /// before the order aborts the attempt instead of executing stale sizing.
    /// </summary>
    private async Task<PositionActionResult> RunConfirmedExitAsync(
        Position position,
        decimal quantity,
        string placedKey,
        CancellationToken ct)
    {
        var request = await BuildExitRequestAsync(position, quantity, ct);

        var validationError = FindValidationError(request);
        if (validationError is not null)
        {
            return PositionActionResult.Fail(validationError);
        }

        var rejection = await FindRiskRejectionAsync(request, ct);
        if (rejection is not null)
        {
            return PositionActionResult.Fail(rejection);
        }

        if (!await _confirmation.RequestConfirmationAsync(_composer.ForPlace(request), ct))
        {
            _logger.LogInformation(
                "Position actions: the user refused the confirmation; nothing placed");
            return PositionActionResult.Declined();
        }

        var fresh = await FindOpenPositionAsync(position.Symbol, ct);
        var freshnessRejection = FindFreshnessRejection(position, fresh);
        if (freshnessRejection is not null)
        {
            return PositionActionResult.Fail(freshnessRejection);
        }

        var order = await PlaceOrderAsync(request, ct);
        return PositionActionResult.PlacedOk(
            [order],
            _localization.GetString(placedKey, FormatQuantity(quantity), position.Symbol.Normalized, order.Id));
    }

    /// <summary>
    /// The body of every public action, wrapped so any fault — a network
    /// exception, an adapter refusing an unsupported shape, or the flow's own
    /// cancellation while the panel closes — resolves into the localized
    /// failure line instead of escaping into an unobserved command exception.
    /// </summary>
    private async Task<PositionActionResult> RunActionAsync(
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
                _logger.LogInformation(ex, "Position actions: the action was cancelled before completing");
            }
            else
            {
                _logger.LogError(ex, "Position actions: the action failed before completing");
            }

            return PositionActionResult.Fail(_localization.GetString(ActionFailedKey, ex.Message));
        }
    }

    /// <summary>
    /// One reduce-only market exit request priced at the freshest reference
    /// close: the anchor the risk manager evaluates the notional on (a bare
    /// market order carries no price and is explicitly rejected by the
    /// shipped manager) and the level the paper adapter fills the market leg
    /// at anyway — the request shape the trading seam already maps.
    /// </summary>
    private async Task<OrderRequest> BuildExitRequestAsync(
        Position position,
        decimal quantity,
        CancellationToken ct)
    {
        var reference = await MarketReference.GetLatestCloseAsync(_trading, position.Symbol, ct);
        return MarketRequest(position.Symbol, ExitSideOf(position), quantity, reduceOnly: true, reference);
    }

    /// <summary>
    /// A market order with an explicit price anchor. On a market leg the
    /// anchor is indicatory (the paper adapter fills at exactly it; live
    /// adapters map the same validated request shape) — it exists so the
    /// risk gate can evaluate the notional, and so it is never a defaulted
    /// number: it is the market's own freshest reference close.
    /// </summary>
    private static OrderRequest MarketRequest(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        bool reduceOnly,
        Price reference) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = new Quantity(quantity),
            Price = reference,
            ReduceOnly = reduceOnly,
        };

    /// <summary>
    /// The side that exits a position: an undefined side is never defaulted
    /// into an entry (trading-safety rule) — it is an explicit refusal.
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
    /// The open position on a symbol as the adapter serves it RIGHT NOW:
    /// the fresh moment every action sizes and guards against (a zero or
    /// missing position reads as none). Thread-safe without locks — each
    /// call is its own read of the adapter's served state.
    /// </summary>
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
    /// The intrinsic request matrix: an unparsable request never reaches any
    /// gate, let alone an adapter.
    /// </summary>
    private string? FindValidationError(OrderRequest request)
    {
        try
        {
            request.Validate();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Position actions: the built order request is invalid and was never sent to a gate");
            return _localization.GetString(ActionFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// The risk gate: an unapproved order is a localized rejection and never
    /// reaches a confirmation dialog or an adapter — exactly like the
    /// new-order dialog's pipeline.
    /// </summary>
    private async Task<string?> FindRiskRejectionAsync(
        OrderRequest request,
        CancellationToken ct)
    {
        var risk = await _riskManager.CheckOrderAsync(request, ct);
        if (risk.IsApproved)
        {
            return null;
        }

        _logger.LogWarning(
            "Position actions: risk controls rejected the {Side} {Type} {Quantity} {Symbol} leg: {Reason}",
            request.Side,
            request.Type,
            request.Quantity.Value,
            request.Symbol.Normalized,
            risk.Reason);
        return _localization.GetString(ActionRejectedKey, risk.Reason ?? string.Empty);
    }

    /// <summary>
    /// The pre-placement freshness guard of a confirmed exit: the position
    /// must still exist with exactly the side and quantity the confirmation
    /// named. A fill that landed first wins and is reported — the vanished
    /// position is the explicit close-on-empty rejection, a changed one is
    /// the race report; both leave nothing on the surface.
    /// </summary>
    private string? FindFreshnessRejection(
        Position confirmed,
        Position? fresh)
    {
        if (fresh is null)
        {
            _logger.LogInformation(
                "Position actions: {Symbol} has no open position left before placement; nothing placed",
                confirmed.Symbol.Normalized);
            return _localization.GetString(NoPositionKey, confirmed.Symbol.Normalized);
        }

        if (fresh.Side != confirmed.Side || fresh.Quantity.Value != confirmed.Quantity.Value)
        {
            _logger.LogInformation(
                "Position actions: {Symbol} changed from {ConfirmedQuantity} to {FreshQuantity} before placement; nothing placed",
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
            await _orderHistory.RecordPlacedAsync(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Position actions: failed to persist {OrderId} to the order history",
                order.Id);
        }

        return order;
    }

    /// <summary>A failed attempt carrying a template key filled with arguments.</summary>
    private PositionActionResult FailWith(
        string messageKey,
        params object[] arguments) =>
        PositionActionResult.Fail(_localization.GetString(messageKey, arguments));

    /// <summary>Parse the reduce amount: a strictly positive invariant-culture decimal. Empty (whitespace), unparsable, zero and negative amounts are one rejection family — never a defaulted size.</summary>
    private static bool TryParsePositiveDecimal(
        string? rawInput,
        out decimal value)
    {
        value = decimal.Zero;
        var raw = rawInput?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return false;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            && value > decimal.Zero;
    }

    /// <summary>Format a quantity with the invariant culture for messages and confirmations.</summary>
    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString(CultureInfo.InvariantCulture);

    #endregion Private Methods
}
