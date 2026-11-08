namespace ZettaTrade.App.ViewModels;

using System;
using System.Globalization;
using Livery.Contracts;
using Livery.Localization;
using ReactiveUI;
using ZettaTrade.Core.Enums;
using ZettaTrade.Strategies;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// One signal rail row for display (T-112): the rail's newest-line render
/// of one strategy signal, carrying the prefill FACTS (symbol, side,
/// close price), the localized reading label, and the click affordance the
/// window lands on the order-ticket prefill path. A row is immutable —
/// rows rebuild per coalesce window, never mutate — and the tooltip/side
/// texts compose AT ARRIVAL from the localization catalog (the
/// <see cref="NotificationRow"/> snapshot discipline: a language switch
/// lands on rows built later, never re-inks stale ones).
/// </summary>
public sealed class SignalRailRow
{
    #region Resx Keys

    /// <summary>The reading label: long-entry rows.</summary>
    internal const string StanceLongKey = "Signal.Rail.StanceLong";

    /// <summary>The reading label: flat/exit rows.</summary>
    internal const string StanceFlatKey = "Signal.Rail.StanceFlat";

    /// <summary>The composed tooltip: format key, strategy, symbol, close price.</summary>
    internal const string RowTooltipKey = "Signal.Rail.Row.Tooltip";

    /// <summary>Price texts render invariant (the ticket's own price format).</summary>
    private const string PriceFormat = "0.00########";

    /// <summary>The row's moment text (the shared order-timestamp column format).</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    #endregion Resx Keys

    #region Properties

    /// <summary>The stamped signal the row renders (the inert event itself).</summary>
    public StrategySignal Source { get; }

    /// <summary>The strategy id the reading belongs to.</summary>
    public string StrategyId => Source.StrategyId;

    /// <summary>The ticket side the stance maps onto: long buys, flat sells.</summary>
    public OrderSide Side => Source.Stance == StrategyStance.Long ? OrderSide.Buy : OrderSide.Sell;

    /// <summary>The signal candle's close price: the ticket's price prefill source.</summary>
    public decimal Price => Source.Candle.Close;

    /// <summary>The stance's visual pole (the row accent + the ticket side's direction).</summary>
    public bool IsEntryLong => Source.Stance == StrategyStance.Long;

    /// <summary>The flat stance's flag (the row accent's down-pole style).</summary>
    public bool IsEntryFlat => Source.Stance == StrategyStance.Flat;

    /// <summary>The composed reading label (localized at the row's arrival).</summary>
    public string StanceLabel { get; }

    /// <summary>The signal candle's open time, ordered-column format.</summary>
    public string CandleTime { get; }

    /// <summary>The composed tooltip: what this row is and what a click does and does not do.</summary>
    public string Tooltip { get; }

    /// <summary>The prefill command: raises the row over the sink (never sends).</summary>
    public ReactiveCommand<RxVoid, RxVoid> PrefillCommand { get; }

    #endregion Properties

    #region Constructor

    private SignalRailRow(
        StrategySignal source,
        ILocalizationManager localization,
        ISignalRailPrefillSink sink)
    {
        Source = source;
        _symbolText = source.Symbol.Normalized;
        StanceLabel = localization.GetString(
            source.Stance == StrategyStance.Long ? StanceLongKey : StanceFlatKey);
        CandleTime = source.Candle.Time.ToString(
            TimeFormat,
            CultureInfo.InvariantCulture);
        Tooltip = localization.GetString(
            RowTooltipKey,
            source.StrategyId,
            _symbolText,
            source.Candle.Close.ToString(PriceFormat, CultureInfo.InvariantCulture),
            CandleTime);
        PrefillCommand = ReactiveCommand.Create<RxVoid>(_ => sink.RaisePrefill(this));
    }

    #endregion Constructor

    #region Factory

    /// <summary>
    /// Build one display row over a stamped signal: the side label and the
    /// tooltip compose now from the event's facts, and the prefill command
    /// wires through the sink.
    /// </summary>
    /// <param name="source">The stamped signal the row renders.</param>
    /// <param name="localization">The localization catalog the texts compose from.</param>
    /// <param name="sink">The prefill sink the click raises against.</param>
    /// <returns>The display row.</returns>
    public static SignalRailRow For(
        StrategySignal source,
        ILocalizationManager localization,
        ISignalRailPrefillSink sink) =>
        new(
            source,
            localization,
            sink ?? throw new ArgumentNullException(nameof(sink)));

    #endregion Factory

    #region Display

    /// <summary>The symbol's normalized text rendered on the row.</summary>
    public string SymbolText => _symbolText;

    /// <summary>The close price's invariant text (the ticket's decimal appearance).</summary>
    public string PriceText => Source.Candle.Close.ToString(PriceFormat, CultureInfo.InvariantCulture);

    private readonly string _symbolText;

    #endregion Display
}
