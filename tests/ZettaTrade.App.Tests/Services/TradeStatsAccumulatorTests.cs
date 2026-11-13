namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Golden tests for the fill-pair aggregation math: open→close round trips
/// (win rate, profit factor, averages, count, volume), the drawdown over
/// the equity curve including its zero-seeded origin, the paper-ledger
/// bookkeeping semantics (a partial close never re-averages the remainder,
/// a flip stamps its fresh position at its own fill), breakeven closes that
/// stay undecided, per-symbol positions that never cross, non-filled rows
/// that cost nothing, the FilledQuantity partial-fill reading, empty input,
/// the identical outcome whether the pages arrive at once or one page
/// at a time (the incremental snapshot equals the batch), and the tag
/// lane (T-211): the outcome-only gate that leaves the position state
/// whole and the volume scope-wide.
/// </summary>
public sealed class TradeStatsAccumulatorTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    /// <summary>
    /// One journal row the way the paper ledger records whole-order fills:
    /// Filled, the (whole) FilledQuantity at the row price, plus the row's
    /// user annotations (T-211) as the journal keeps them.
    /// </summary>
    private static OrderPageRow Fill(
        OrderSide side,
        decimal quantity,
        decimal price,
        int minute,
        string symbol = "BTC/USDT",
        OrderStatus status = OrderStatus.Filled,
        decimal? filledQuantity = null,
        string? note = null,
        string? tags = null) => new(
        new Order
        {
            Id = $"row-{minute:n0}",
            ExchangeId = "paper",
            Symbol = Symbol.Parse(symbol),
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = BaseTime.AddMinutes(minute),
            FilledQuantity = new Quantity(filledQuantity ?? quantity),
        },
        note,
        tags);

    #endregion Helpers

    #region Golden Round Trips

    [Fact]
    public void SingleWin_Buy1At10Sell1At12_BooksOneTradeAndTheEquityAnchor()
    {
        // Buy 1 @ 10 opens; sell 1 @ 12 closes: realized +2, volume 10 + 12.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
        ]);

        var summary = accumulator.Snapshot();

        accumulator.HasAnyFills.Should().BeTrue();
        summary.Trades.Should().Be(1);
        summary.Wins.Should().Be(1);
        summary.Losses.Should().Be(0);
        summary.RealizedTotal.Should().Be(2m);
        summary.Volume.Should().Be(22m);
        summary.AverageWinner.Should().Be(2m);
        summary.AverageLoser.Should().BeNull("no losing close exists");
        summary.WinRate.Should().Be(1m);
        summary.ProfitFactor.Should().BeNull(
            "an unbounded factor is a null the view renders as infinity");
        summary.Equity.Should().Equal(
            new EquityCurvePoint(BaseTime.AddMinutes(0), 0m),
            new EquityCurvePoint(BaseTime.AddMinutes(1), 2m));
    }

    [Fact]
    public void MixedPlusTwoMinusOne_SetsProfitFactorOneHalloAndDrawdown()
    {
        // A +2 winner then a -1 loser: realized +1; the equity peak 2 falls
        // to 1 — drawdown 1.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 9m, 3),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(2);
        summary.Wins.Should().Be(1);
        summary.Losses.Should().Be(1);
        summary.RealizedTotal.Should().Be(1m);
        summary.AverageWinner.Should().Be(2m);
        summary.AverageLoser.Should().Be(-1m);
        summary.WinRate.Should().Be(0.5m);
        summary.ProfitFactor.Should().Be(2m);
        summary.MaxDrawdown.Should().Be(1m);
        summary.Equity.Select(point => point.Value).Should().Equal([0m, 2m, 1m]);
    }

    [Fact]
    public void PartialClose_NeverReAveragesTheRemainder()
    {
        // Open 2 @ 9; sell 1 @ 12 (+3); sell 1 @ 10 (+1, the average entry
        // of the remainder stayed 9 — a re-averaged remainder would realize
        // -1/2 here instead of the +1).
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 2m, 9m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
            Fill(OrderSide.Sell, 1m, 10m, 2),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(2);
        summary.Wins.Should().Be(2);
        summary.Losses.Should().Be(0);
        summary.RealizedTotal.Should().Be(4m);
        summary.AverageWinner.Should().Be(2m, "(+3 and +1 average over the two wins)");
        summary.MaxDrawdown.Should().Be(0m);
        summary.Equity.Select(point => point.Value).Should().Equal([0m, 3m, 4m]);
    }

    [Fact]
    public void Flip_BooksThreeTradeOutcomesAndSeedsTheFreshPositionAtItsOwnFill()
    {
        // Buy 1 @ 10 → sell 2 @ 13 (+3, flips remainder into -1 @ 13) →
        // buy 2 @ 15 (-2, flips into +1 @ 15) → sell 1 @ 14 (-1). Trades 3:
        // one win, two losses; equity 0 → +3 → +1 → 0, drawdown 3.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 2m, 13m, 1),
            Fill(OrderSide.Buy, 2m, 15m, 2),
            Fill(OrderSide.Sell, 1m, 14m, 3),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(3);
        summary.Wins.Should().Be(1);
        summary.Losses.Should().Be(2);
        summary.RealizedTotal.Should().Be(0m);
        summary.ProfitFactor.Should().Be(1m, "3 gross profit over 3 gross loss");
        summary.WinRate.Should().Be(1m / 3m);
        summary.AverageWinner.Should().Be(3m);
        summary.AverageLoser.Should().Be(-1.5m);
        summary.MaxDrawdown.Should().Be(3m);
        summary.Equity.Select(point => point.Value).Should().Equal([0m, 3m, 1m, 0m]);
    }

    [Fact]
    public void BreakevenClose_CountsAsTradeButNeverDecidesTheWinRate()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 10m, 1),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(1);
        summary.Wins.Should().Be(0);
        summary.Losses.Should().Be(0);
        summary.RealizedTotal.Should().Be(0m);
        summary.WinRate.Should().BeNull("nothing decided: honest null, never a 0");
        summary.ProfitFactor.Should().BeNull();
        summary.AverageWinner.Should().BeNull();
        summary.AverageLoser.Should().BeNull();
        summary.Equity.Select(point => point.Value).Should().Equal([0m, 0m]);
    }

    [Fact]
    public void ParallelSymbols_KeepIndependentPositionsAndSumTheVolume()
    {
        // BTC open→close and ETH open→close interleave on the timeline;
        // each symbol's position math must never see the other's fills.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0, symbol: "BTC/USDT"),
            Fill(OrderSide.Buy, 2m, 5m, 1, symbol: "ETH/USDT"),
            Fill(OrderSide.Sell, 2m, 6m, 2, symbol: "ETH/USDT"),
            Fill(OrderSide.Sell, 1m, 12m, 3, symbol: "BTC/USDT"),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(2);
        summary.Wins.Should().Be(2);
        summary.RealizedTotal.Should().Be(4m, "(+2 BTC and +2 ETH)");
        summary.Volume.Should().Be(10m + 10m + 12m + 12m);
        summary.Equity.Select(point => point.Value).Should().Equal([0m, 2m, 4m]);
    }

    #endregion Golden Round Trips

    #region Input Guardians

    [Fact]
    public void OnlyNonFilledRows_CostsNothing()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0, status: OrderStatus.Pending),
            Fill(OrderSide.Sell, 1m, 12m, 1, status: OrderStatus.Cancelled),
            Fill(OrderSide.Buy, 1m, 10m, 2, status: OrderStatus.Rejected),
            Fill(OrderSide.Sell, 1m, 12m, 3, status: OrderStatus.Expired),
        ]);

        var summary = accumulator.Snapshot();

        accumulator.HasAnyFills.Should().BeFalse();
        summary.Trades.Should().Be(0);
        summary.Wins.Should().Be(0);
        summary.Losses.Should().Be(0);
        summary.RealizedTotal.Should().Be(0m);
        summary.Volume.Should().Be(0m);
        summary.MaxDrawdown.Should().Be(0m);
        summary.WinRate.Should().BeNull();
        summary.ProfitFactor.Should().BeNull();
        summary.AverageWinner.Should().BeNull();
        summary.AverageLoser.Should().BeNull();
        summary.Equity.Should().BeEmpty("no fill, no fabricated curve origin");
    }

    [Fact]
    public void FilledRowWithPartialFillQuantity_ReadsOnlyActuallyFilledSize()
    {
        // A live-adapter partial row: Quantity 2 but FilledQuantity 1 — the
        // two 1-sized halves book as separate fills (open + close at -1 per
        // unit below the entry, i.e. one loss of -2 realized over 2 units
        // NOT booked at once).
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            new OrderPageRow(
                new Order
                {
                    Id = "partial-1",
                    ExchangeId = "paper",
                    Symbol = Symbol.Parse("BTC/USDT"),
                    Side = OrderSide.Buy,
                    Type = OrderType.Limit,
                    Quantity = new Quantity(2m),
                    Price = new Price(10m),
                    Status = OrderStatus.Filled,
                    TimeInForce = TimeInForce.GTC,
                    CreatedAt = BaseTime,
                    FilledQuantity = new Quantity(1m),
                },
                null,
                null),
            new OrderPageRow(
                new Order
                {
                    Id = "partial-2",
                    ExchangeId = "paper",
                    Symbol = Symbol.Parse("BTC/USDT"),
                    Side = OrderSide.Sell,
                    Type = OrderType.Limit,
                    Quantity = new Quantity(2m),
                    Price = new Price(8m),
                    Status = OrderStatus.Filled,
                    TimeInForce = TimeInForce.GTC,
                    CreatedAt = BaseTime.AddMinutes(1),
                    FilledQuantity = new Quantity(1m),
                },
                null,
                null),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(1);
        summary.Losses.Should().Be(1);
        summary.RealizedTotal.Should().Be(-2m);
        summary.Volume.Should().Be(18m, "1 × 10 + 1 × 8, the ADVISORY quantity 2 never doubles it");
    }

    [Fact]
    public void FilledQuantityAboveZeroGuard_ZeroFillAddsNothing()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 2m, 10m, 0, filledQuantity: 0m),
            Fill(OrderSide.Sell, 2m, 12m, 1, filledQuantity: 0m),
        ]);

        var summary = accumulator.Snapshot();

        accumulator.HasAnyFills.Should().BeFalse("a zero fill is not a fill");
        summary.Trades.Should().Be(0);
        summary.Volume.Should().Be(0m);
        summary.Equity.Should().BeEmpty();
    }

    [Fact]
    public void EmptyIngest_LeavesTheHonestZeroState()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest([]);

        accumulator.HasAnyFills.Should().BeFalse();
        var summary = accumulator.Snapshot();
        summary.Trades.Should().Be(0);
        summary.Equity.Should().BeEmpty();
        summary.WinRate.Should().BeNull();
        summary.ProfitFactor.Should().BeNull();
    }

    [Fact]
    public void PageByPageIngest_MatchesTheBatchIngest()
    {
        // The dashboard scans page by page and publishes a snapshot after
        // every page: after page 1 the tiles already carry the partial
        // truth, and the page-2 outcome must equal the one-shot ingest.
        var fills = new[]
        {
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 9m, 3),
        };

        var incremental = new TradeStatsAccumulator();
        incremental.Ingest(fills[..2]);
        var partial = incremental.Snapshot();
        partial.Trades.Should().Be(1);
        partial.RealizedTotal.Should().Be(2m);
        partial.Losses.Should().Be(0);
        partial.Equity.Should().HaveCount(2, "anchor plus the first close");

        incremental.Ingest(fills[2..]);

        var batch = new TradeStatsAccumulator();
        batch.Ingest(fills);

        incremental.Snapshot().Should().BeEquivalentTo(batch.Snapshot());
    }

    #endregion Input Guardians

    #region Realized Ledger (T-210)

    [Fact]
    public void RealizedLedger_BooksOneRowPerClosingFill_WithTheFillPairMath()
    {
        // The flip walk (the golden above): three closing fills, three
        // ledger rows — the exact outcomes the summary and the curve see.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 2m, 13m, 1),
            Fill(OrderSide.Buy, 2m, 15m, 2),
            Fill(OrderSide.Sell, 1m, 14m, 3),
        ]);

        var ledger = accumulator.GetRealizedTrades();

        ledger.Should().Equal(
            new StatementRealizedTrade(
                BaseTime.AddMinutes(1), "paper", "BTC/USDT", "Sell", "row-1",
                1m, 10m, 13m, 3m, "USDT", null, null),
            new StatementRealizedTrade(
                BaseTime.AddMinutes(2), "paper", "BTC/USDT", "Buy", "row-2",
                1m, 13m, 15m, -2m, "USDT", null, null),
            new StatementRealizedTrade(
                BaseTime.AddMinutes(3), "paper", "BTC/USDT", "Sell", "row-3",
                1m, 15m, 14m, -1m, "USDT", null, null));
    }

    [Fact]
    public void RealizedLedger_SnapshotIsAFreshCopy_TheScanKeepsAppending()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
        ]);

        var first = accumulator.GetRealizedTrades();

        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 9m, 3),
        ]);

        first.Should().HaveCount(1, "a snapshot must not see the rows booked after it");
        accumulator.GetRealizedTrades().Should().HaveCount(2);
    }

    [Fact]
    public void RealizedLedger_QuotelessSymbol_CarriesTheHonestUnknownQuote()
    {
        // A stock symbol carries no quote asset: the row books the outcome
        // with a null quote currency — the honest unknown, never a
        // fabricated currency.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 10m, 190m, 0, symbol: "AAPL"),
            Fill(OrderSide.Sell, 10m, 195m, 1, symbol: "AAPL"),
        ]);

        var ledger = accumulator.GetRealizedTrades();

        ledger.Should().ContainSingle().Which.QuoteCurrency.Should().BeNull();
        ledger[0].RealizedPnl.Should().Be(50m);
        ledger[0].Symbol.Should().Be("AAPL");
    }

    #endregion Realized Ledger (T-210)

    #region Tag Lane (T-211)

    [Fact]
    public void TagLane_ClosesOutsideTheLane_BookTheStateNotTheOutcome()
    {
        // The lane "swing" filters the OUTCOME only: the untagged close
        // still reduces the position state (the pairing stays whole), but
        // books no trade, no PnL, no curve point, no ledger row. The
        // volume keeps counting every fill in the scope (the disclosed
        // scope-wide fact).
        var accumulator = new TradeStatsAccumulator("swing");
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
        ]);

        var summary = accumulator.Snapshot();

        accumulator.HasAnyFills.Should().BeTrue("the lane filters outcomes, never the scope itself");
        summary.Trades.Should().Be(0);
        summary.Wins.Should().Be(0);
        summary.Losses.Should().Be(0);
        summary.RealizedTotal.Should().Be(0m);
        summary.Volume.Should().Be(22m, "the volume stays the scope's fill volume");
        summary.WinRate.Should().BeNull();
        summary.Equity.Should().ContainSingle("the zero-seeded origin, no outcome points");
        summary.Equity[0].Value.Should().Be(0m);
        accumulator.GetRealizedTrades().Should().BeEmpty();
    }

    [Fact]
    public void TagLane_MatchesTheExactTokenCaseInsensitively()
    {
        // The exact-token match over the canonical comma-joined list:
        // "swing" books; "swing-trade" (a different token) does not; the
        // case differences never split a lane. The constructor trims stray
        // whitespace off the lane token.
        var tagged = Fill(OrderSide.Sell, 1m, 12m, 1, tags: "Swing, Scalp");

        var lane = new TradeStatsAccumulator(" swing ");
        lane.Ingest([Fill(OrderSide.Buy, 1m, 10m, 0), tagged]);
        lane.Snapshot().Trades.Should().Be(1, "the lane matches the exact token, case-insensitively");

        var narrowLane = new TradeStatsAccumulator("swing-trade");
        narrowLane.Ingest([Fill(OrderSide.Buy, 1m, 10m, 0), tagged]);
        narrowLane.Snapshot().Trades.Should().Be(0, "a partial token is not the token");

        var caseLane = new TradeStatsAccumulator("SWING");
        caseLane.Ingest([Fill(OrderSide.Buy, 1m, 10m, 0), tagged]);
        caseLane.Snapshot().Trades.Should().Be(1, "the match is case-insensitive");
    }

    [Fact]
    public void TagLane_TheStateBooksRegardless_TheNextLaneTradeSeesTheWholePairing()
    {
        // The honesty core: an untagged close reduces the position state, so
        // the next tagged close pairs against the true remainder — the lane
        // never fabricates a PnL from a close whose open it would have
        // filtered away. Buy 1@10, untagged close 1@12 (state flat, no
        // outcome), re-open 1@10, tagged close 1@14: the lane trade books
        // +4 (entry 10), not +8.
        var accumulator = new TradeStatsAccumulator("swing");
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 14m, 3, tags: "swing"),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(1);
        summary.Wins.Should().Be(1);
        summary.RealizedTotal.Should().Be(4m, "the lane trade pairs against the true state");
        summary.Volume.Should().Be(46m, "10 + 12 + 10 + 14, the scope-wide fills");
        var trade = accumulator.GetRealizedTrades().Should().ContainSingle().Subject;
        trade.RealizedPnl.Should().Be(4m);
        trade.EntryPrice.Should().Be(10m);
        trade.ExitPrice.Should().Be(14m);
    }

    [Fact]
    public void TagLane_TheLedgerCarriesTheClosingRowsIdentityAndAnnotations()
    {
        // Lane trades stamp the closing journal row's identity (the
        // annotation surface's key) and the row's note and tags exactly as
        // the journal keeps them.
        var accumulator = new TradeStatsAccumulator("swing");
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(
                OrderSide.Sell, 1m, 12m, 1,
                note: "scaled out at the level",
                tags: "swing,scalp"),
        ]);

        var trade = accumulator.GetRealizedTrades().Should().ContainSingle().Subject;

        trade.ClosingOrderId.Should().Be("row-1");
        trade.Note.Should().Be("scaled out at the level");
        trade.Tags.Should().Be("swing,scalp");
        trade.ClosingSide.Should().Be("Sell");
        trade.RealizedPnl.Should().Be(2m);
    }

    [Fact]
    public void NullLane_BooksEveryOutcome_TheDefaultScope()
    {
        // The null lane (the empty text) is the default scope: every
        // closing fill books, tagged or not.
        var accumulator = new TradeStatsAccumulator(null);
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1, tags: "swing"),
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 9m, 3),
        ]);

        var summary = accumulator.Snapshot();

        summary.Trades.Should().Be(2);
        summary.RealizedTotal.Should().Be(1m, "(+2 then -1)");
    }

    #endregion Tag Lane (T-211)

    #region Snapshot Pinning

    [Fact]
    public void EquityCurve_SnapshotIsPinned_TheScanKeepsAppending()
    {
        // The dashboard publishes a snapshot after every page and keeps
        // scanning: the published curve must stay exactly as it was at its
        // publish (the anchor plus the closes booked so far), never grow
        // with the scan's later appends.
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
        ]);

        var first = accumulator.Snapshot();

        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 2),
            Fill(OrderSide.Sell, 1m, 9m, 3),
        ]);

        first.Equity.Should().HaveCount(2, "the anchor plus the first close only");
        first.Equity.Select(point => point.Value).Should().Equal([0m, 2m]);
        accumulator.Snapshot().Equity.Should().HaveCount(3, "the anchor plus both closes");
    }

    [Fact]
    public void EquityCurve_IndexOutOfRange_ThrowsTheHonestGuard()
    {
        var accumulator = new TradeStatsAccumulator();
        accumulator.Ingest(
        [
            Fill(OrderSide.Buy, 1m, 10m, 0),
            Fill(OrderSide.Sell, 1m, 12m, 1),
        ]);

        var pinned = accumulator.Snapshot().Equity;

        var act = () => _ = pinned[2];
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion Snapshot Pinning
}
