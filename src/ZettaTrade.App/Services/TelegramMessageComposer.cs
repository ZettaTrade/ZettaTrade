namespace ZettaTrade.App.Services;

using System;
using System.Globalization;
using Livery.Localization;
using ZettaTrade.Core.Enums;

/// <summary>
/// The Telegram trade message's one composer (T-041): the composed text a
/// journaled trade outcome pushes to the user's bot chat. The facts are the
/// journal row's own — symbol, side word, quantity, price, the row's
/// exchange id and the trading lane's tag — so the message never fabricates
/// a fact the event surface does not carry; a fill without a price fact is
/// a surface-contract fault (never a fabricated zero). Text composes from
/// the localization catalog at send time: the message rides the language
/// the app holds when the outcome arrives.
/// </summary>
internal static class TelegramMessageComposer
{
    #region Resx Keys

    /// <summary>The fill line: side word, quantity, symbol, price, exchange, lane tag.</summary>
    private const string FillKey = "Notif.TelegramFill";

    /// <summary>The cancellation line with the symbol (the journal recovered it).</summary>
    private const string CancelOnKey = "Notif.TelegramCancelOn";

    /// <summary>The cancellation line without a symbol (the journal could not recover it).</summary>
    private const string CancelKey = "Notif.TelegramCancel";

    /// <summary>The rejection line: symbol, the adapter's own reason, exchange, lane tag.</summary>
    private const string RejectKey = "Notif.TelegramReject";

    /// <summary>The trading lane's tag words (the status-bar chip's own vocabulary, reused).</summary>
    private const string LanePaperKey = "Trading.Lane.Paper";
    private const string LaneTestnetKey = "Trading.Lane.Testnet";
    private const string LaneLiveKey = "Trading.Lane.Live";

    /// <summary>The invariant number format the notification rows already render facts in (the T-097 discipline).</summary>
    private const string DecimalFormat = "F8";

    #endregion Resx Keys

    #region Composition

    /// <summary>
    /// Compose one trade message from the event's structured facts. Only
    /// the three trade outcomes compose (the bridge filters the rest); any
    /// other kind is a pipeline design fault, never a silently substituted
    /// message — the trading-safety canon.
    /// </summary>
    /// <param name="source">The raw journaled-outcome event.</param>
    /// <param name="lane">The trading lane the outcome arrived under (the message's paper/live tag).</param>
    /// <param name="localization">The localization catalog the line composes from.</param>
    /// <returns>The message text.</returns>
    internal static string Compose(Notification source, TradingLane lane, ILocalizationManager localization) => source.Kind switch
    {
        NotificationKind.OrderFill => localization.GetString(
            FillKey,
            localization.GetString(SideKey(source.Side)),
            source.Quantity.ToString(DecimalFormat, CultureInfo.InvariantCulture),
            source.SymbolName,
            FillPriceText(source),
            source.Exchange,
            localization.GetString(LaneKey(lane))),
        NotificationKind.OrderCancel when source.SymbolName.Length > 0 => localization.GetString(
            CancelOnKey,
            source.SymbolName,
            source.Exchange,
            localization.GetString(LaneKey(lane))),
        NotificationKind.OrderCancel => localization.GetString(
            CancelKey,
            source.Exchange,
            localization.GetString(LaneKey(lane))),
        NotificationKind.OrderReject => localization.GetString(
            RejectKey,
            source.SymbolName,
            source.Reason,
            source.Exchange,
            localization.GetString(LaneKey(lane))),
        _ => throw new ArgumentOutOfRangeException(
            nameof(source),
            source.Kind,
            "The Telegram sink composes only the journaled trade outcomes."),
    };

    #endregion Composition

    #region Helpers

    /// <summary>The fill's price text: the journaled row's own price. A fill without a price fact is a surface-contract fault (the publish path always carries the row's price) — never a fabricated zero.</summary>
    private static string FillPriceText(Notification source) => source.Price is { } price
        ? price.ToString(DecimalFormat, CultureInfo.InvariantCulture)
        : throw new InvalidOperationException(
            "The fill event carries no price fact; the Telegram message would fabricate one.");

    /// <summary>The side's localized word (the order ticket's own vocabulary, reused).</summary>
    private static string SideKey(OrderSide side) => side switch
    {
        OrderSide.Buy => "Order.Buy",
        OrderSide.Sell => "Order.Sell",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "The side has no localized word."),
    };

    /// <summary>The lane's localized tag word's catalog key (the status-bar chip's own vocabulary, reused; the composer resolves the word).</summary>
    private static string LaneKey(TradingLane lane) => lane switch
    {
        TradingLane.Paper => LanePaperKey,
        TradingLane.Testnet => LaneTestnetKey,
        TradingLane.Live => LaneLiveKey,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "The lane has no localized tag word."),
    };

    #endregion Helpers
}
