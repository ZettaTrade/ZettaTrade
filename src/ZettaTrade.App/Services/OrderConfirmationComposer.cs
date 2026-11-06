namespace ZettaTrade.App.Services;

using System.Globalization;
using Livery.Localization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Composes the structured confirmation requests for order operations, so
/// the new-order dialog (placement) and the positions panel (cancellation)
/// present summaries in exactly the shape the MCP trading tools use. Enum
/// labels resolve through <see cref="ILocalizationManager"/>; callers never
/// embed user-facing text themselves.
/// </summary>
public sealed class OrderConfirmationComposer
{
    #region Resx Keys

    private const string ConfirmPlaceOrderKey = "Trading.ConfirmPlaceOrderFormat";
    private const string ConfirmCancelOrderKey = "Positions.ConfirmCancelFormat";
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
}

