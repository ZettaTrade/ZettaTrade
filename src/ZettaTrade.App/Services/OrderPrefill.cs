namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// A deliberate order dialog prefill: the inputs ride the user's trading
/// action (a clicked order book level today — bids buy, asks sell) instead
/// of free typing. The quantity is never carried: the dialog opens with it
/// empty, and every pre-filled field stays editable.
/// </summary>
public sealed record OrderPrefill(Symbol Symbol, OrderSide Side, decimal Price);
