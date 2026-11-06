namespace ZettaTrade.App.Services;

using System.Globalization;
using Livery.Localization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Composes the structured confirmation requests for order operations, so
/// the new-order dialog (placement) and the positions panel (cancellation,
/// order amending) present summaries in exactly the shape the MCP trading
/// tools use. Enum labels resolve through <see cref="ILocalizationManager"/>;
/// callers never embed user-facing text themselves.
/// </summary>
public sealed class OrderConfirmationComposer
{
    #region Resx Keys

    private const string ConfirmPlaceOrderKey = "Trading.ConfirmPlaceOrderFormat";
    private const string ConfirmCancelOrderKey = "Positions.ConfirmCancelFormat";
    private const string ConfirmAmendPriceKey = "Positions.ConfirmAmend";
    private const string ConfirmAmendQuantityKey = "Positions.ConfirmAmendQuantityOnly";
    private const string ConfirmReverseKey = "Positions.ConfirmReverseFormat";
    private const string MarketLabelKey = "Trading.PriceMarket";

    #endregion Resx Keys

    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Create a confirmation composer.
    /// </summary>
    public OrderConfirmationComposer(ILocalizationManager localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>
    /// Confirmation summary shown before an order is placed: the same
    /// argument order as the MCP trading tools — side, type, quantity,
    /// symbol, price. The price argument is the anchor the order commits
    /// to: its limit price when it has one, else its stop trigger (stop
    /// orders summarize at the level whose crossing converts them into an
    /// executable commitment, T-074), else the market-price label.
    /// </summary>
    public ConfirmationRequest ForPlace(OrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sideText = _localization.GetString($"Order.{request.Side}");
        var typeText = _localization.GetString($"Order.{request.Type}");
        var anchor = request.Price ?? request.StopPrice;
        var priceText = anchor is { } price
            ? price.Value.ToString("F8", CultureInfo.InvariantCulture)
            : _localization.GetString(MarketLabelKey);

        return new ConfirmationRequest
        {
            MessageKey = ConfirmPlaceOrderKey,
            Arguments =
            [
                sideText,
                typeText,
                request.Quantity.Value.ToString(CultureInfo.InvariantCulture),
                request.Symbol.Normalized,
                priceText,
            ],
        };
    }

    /// <summary>
    /// Confirmation summary shown before a position reverse runs: ONE
    /// mandatory confirmation naming both legs — the reduce-only exit and
    /// the opposite-side re-entry — in template order exit side, exit
    /// quantity, symbol, entry side, entry quantity. The reverse is a
    /// position change, so a refusal of this single summary must leave
    /// nothing on the surface.
    /// </summary>
    /// <param name="exit">The reduce-only exit leg of the reverse.</param>
    /// <param name="entry">The opposite-side re-entry leg of the reverse.</param>
    public ConfirmationRequest ForReverse(
        OrderRequest exit,
        OrderRequest entry)
    {
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(entry);

        var exitSideText = _localization.GetString($"Order.{exit.Side}");
        var entrySideText = _localization.GetString($"Order.{entry.Side}");

        return new ConfirmationRequest
        {
            MessageKey = ConfirmReverseKey,
            Arguments =
            [
                exitSideText,
                exit.Quantity.Value.ToString(CultureInfo.InvariantCulture),
                exit.Symbol.Normalized,
                entrySideText,
                entry.Quantity.Value.ToString(CultureInfo.InvariantCulture),
            ],
        };
    }

    /// <summary>
    /// Confirmation summary shown before an order is cancelled: side,
    /// order id, quantity, symbol. The order id disambiguates when several
    /// orders exist for the same symbol.
    /// </summary>
    public ConfirmationRequest ForCancel(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var sideText = _localization.GetString($"Order.{order.Side}");

        return new ConfirmationRequest
        {
            MessageKey = ConfirmCancelOrderKey,
            Arguments =
            [
                sideText,
                order.Id,
                order.Quantity.Value.ToString(CultureInfo.InvariantCulture),
                order.Symbol.Normalized,
            ],
        };
    }

    /// <summary>
    /// Confirmation summary shown before a working order is amended: the
    /// same leading identity shape the cancel confirmation uses (side, id)
    /// plus the quantity delta, and — when the limit price is part of the
    /// edit — the price delta too. The template always names the mechanics
    /// honestly (cancel+new against the shipped seam), never "replace".
    /// </summary>
    /// <param name="order">The working order as the adapter holds it.</param>
    /// <param name="amended">The amended request shape the flow is about to send.</param>
    /// <param name="priceEdited">True when the amend touches the limit price as well.</param>
    public ConfirmationRequest ForAmend(Order order, OrderRequest amended, bool priceEdited)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(amended);

        var sideText = _localization.GetString($"Order.{order.Side}");
        var quantity = order.Quantity.Value.ToString(CultureInfo.InvariantCulture);
        var newQuantity = amended.Quantity.Value.ToString(CultureInfo.InvariantCulture);

        // priceEdited guarantees the amended request carries its new limit.
        var newPriceText = amended.Price is { } amendedPrice
            ? amendedPrice.Value.ToString("F8", CultureInfo.InvariantCulture)
            : string.Empty;

        return priceEdited
            ? new ConfirmationRequest
            {
                MessageKey = ConfirmAmendPriceKey,
                Arguments =
                [
                    sideText,
                    order.Id,
                    quantity,
                    newQuantity,
                    order.Price.Value.ToString("F8", CultureInfo.InvariantCulture),
                    newPriceText,
                    order.Symbol.Normalized,
                ],
            }
            : new ConfirmationRequest
            {
                MessageKey = ConfirmAmendQuantityKey,
                Arguments =
                [
                    sideText,
                    order.Id,
                    quantity,
                    newQuantity,
                    order.Symbol.Normalized,
                ],
            };
    }
}

