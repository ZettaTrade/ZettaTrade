namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The chat workspace context: the exchange name reads the active-exchange
/// facade fresh on every render, every symbol selection re-attaches the
/// ticker and blanks the stale price, the newest tick wins, and a workspace
/// without a symbol renders no time frame even if a standing choice existed
/// (the prompt shows the trader's market or nothing, per part).
/// </summary>
public sealed class ChatContextServiceTests
{
    #region Constants

    /// <summary>The staged bitcoin tick prices (the newest must win).</summary>
    private const decimal BitcoinFirstTick = 50_000.5m;
    private const decimal BitcoinSecondTick = 50_000.75m;

    /// <summary>The staged ethereum tick price for the re-attach fact.</summary>
    private const decimal EthereumFirstTick = 777.5m;

    /// <summary>The staged ticker's 24h change (the tick carries it; the
    /// context renders it alongside the price since the market context
    /// reads both).</summary>
    private const decimal StagedTicker24hChange = 0.4m;

    #endregion

    #region Fixtures

    /// <summary>A staged ticker: the market shape around the price is bridge filler.</summary>
    private static Ticker StagedTicker(Symbol symbol, decimal lastPrice) => new(
        symbol,
        lastPrice,
        Bid: lastPrice - 1m,
        Ask: lastPrice + 1m,
        Volume24h: 12m,
        PriceChange24hPercent: StagedTicker24hChange,
        Timestamp: DateTimeOffset.UtcNow);

    /// <summary>The wired rig: the context and everything that drives it.</summary>
    private sealed record Rig(
        ChatContextService Context,
        IMarketDataAdapter Adapter,
        Subject<Symbol> Symbols,
        Subject<TimeFrame> TimeFrames,
        List<Subject<Ticker>> Tickers,
        string ExchangeName);

    private static Rig CreateRig(string exchangeName = "Demo Paper")
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Name.Returns(exchangeName);
        var tickers = new List<Subject<Ticker>>();
        adapter
            .SubscribeTicker(Arg.Any<Symbol>())
            .Returns(call =>
            {
                var subject = new Subject<Ticker>();
                tickers.Add(subject);

                return subject;
            });

        var symbols = new Subject<Symbol>();
        var timeFrames = new Subject<TimeFrame>();

        // The market-data face serves book frames for the context since the
        // market context rides the book too: the rig's scenarios write no
        // book facts (the empty stream is the no-frame shape).
        adapter
            .SubscribeOrderBook(Arg.Any<Symbol>(), Arg.Any<int>())
            .Returns(Observable.Empty<Core.Entities.OrderBook>());

        var context = new ChatContextService(
            adapter,
            symbols,
            timeFrames,
            NullLogger<ChatContextService>.Instance);

        return new Rig(context, adapter, symbols, timeFrames, tickers, exchangeName);
    }

    #endregion

    #region Snapshot Shape

    /// <summary>
    /// Nothing selected yet: the exchange name is the only workspace part —
    /// symbol, time frame and price stay absent (nothing invented).
    /// </summary>
    [Fact]
    public void Current_BeforeAnythingArrives_RendersTheExchangeOnly()
    {
        // Arrange
        var rig = CreateRig();

        // Act
        var snapshot = rig.Context.Current;

        // Assert
        snapshot.Should().Be(new ChatContext
        {
            ExchangeName = "Demo Paper",
        }, "the workspace parts absent from the streams stay absent from the snapshot");
    }

    /// <summary>
    /// The full workspace: symbol, standing time frame and the latest tick
    /// render together once they arrived.
    /// </summary>
    [Fact]
    public void Current_WithSymbolTimeFrameAndATick_RendersTheFullSnapshot()
    {
        // Arrange
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");

        // Act
        rig.TimeFrames.OnNext(TimeFrame.H4);
        rig.Symbols.OnNext(bitcoin);
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinFirstTick));

        var snapshot = rig.Context.Current;

        // Assert
        snapshot.Should().Be(new ChatContext
        {
            ExchangeName = "Demo Paper",
            Symbol = bitcoin.Normalized,
            TimeFrame = TimeFrame.H4,
            LastPrice = BitcoinFirstTick,
            PriceChange24hPercent = StagedTicker24hChange,
        });
    }

    /// <summary>
    /// A standing time frame with no symbol stays parked (rendered as
    /// nothing); once the symbol lands the standing choice is real again.
    /// </summary>
    [Fact]
    public void Current_ASymbollessTimeFrame_StaysParkedUntilASymbolLands()
    {
        // Arrange
        var rig = CreateRig();

        // Act
        rig.TimeFrames.OnNext(TimeFrame.H4);
        var parked = rig.Context.Current;

        // Assert
        parked.TimeFrame.Should().BeNull("a trader with no market has no frame to show");

        // Act: the symbol arrives — the standing frame is real now
        rig.Symbols.OnNext(Symbol.Parse("ETH/BTC"));

        rig.Context.Current.TimeFrame.Should().Be(TimeFrame.H4);
    }

    /// <summary>
    /// The exchange name reads the facade fresh on every render, so a facade
    /// switch re-renders the snapshot's head without any restart.
    /// </summary>
    [Fact]
    public void Current_TheFacadeSwitchesExchanges_TheSnapshotReadsTheCurrentHead()
    {
        // Arrange
        var rig = CreateRig();

        // Act / Assert
        rig.Context.Current.ExchangeName.Should().Be(rig.ExchangeName);
        rig.Adapter.Name.Returns("Binance");
        rig.Context.Current.ExchangeName.Should().Be("Binance", "the snapshot is read fresh, never cached");
    }

    #endregion

    #region Stream Discipline

    /// <summary>
    /// A symbol change is a new market: the stale price blanks immediately,
    /// the ticker re-attaches to the new symbol (the old feed untouched), and
    /// the newest tick wins from then on.
    /// </summary>
    [Fact]
    public void ASecondSymbol_BlanksTheStalePriceAndReattachesTheTicker()
    {
        // Arrange
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        var ethereum = Symbol.Parse("ETH/BTC");
        rig.Symbols.OnNext(bitcoin);
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinFirstTick));
        rig.Context.Current.LastPrice.Should().Be(BitcoinFirstTick);

        // Act
        rig.Symbols.OnNext(ethereum);

        // Assert: the stale price is gone and the second feed carries the price
        rig.Context.Current.LastPrice.Should().BeNull("a new symbol is a new market; the old price lies");
        rig.Adapter.Received(1).SubscribeTicker(bitcoin);
        rig.Adapter.Received(1).SubscribeTicker(ethereum);

        // Act: the new market ticks
        rig.Tickers[1].OnNext(StagedTicker(ethereum, EthereumFirstTick));

        // Assert
        rig.Context.Current.LastPrice.Should().Be(EthereumFirstTick);
    }

    /// <summary>
    /// The newest tick of the standing symbol wins the snapshot, so the
    /// prompt always quotes the market as it stood at send time.
    /// </summary>
    [Fact]
    public void TheNewestTick_WinsThePrice()
    {
        // Arrange
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);

        // Act
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinFirstTick));
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinSecondTick));

        // Assert
        rig.Context.Current.LastPrice.Should().Be(BitcoinSecondTick);
    }

    /// <summary>
    /// An old ticker that still fires after the switch cannot lie through the
    /// re-attached feed: the price the snapshot holds stays the new market's.
    /// </summary>
    [Fact]
    public void AStaleTicker_LeavesNoFootprintsAfterTheSwitch()
    {
        // Arrange
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        var ethereum = Symbol.Parse("ETH/BTC");
        rig.Symbols.OnNext(bitcoin);
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinFirstTick));
        rig.Symbols.OnNext(ethereum);
        rig.Tickers[1].OnNext(StagedTicker(ethereum, EthereumFirstTick));

        // Act: the pre-switch feed fires one last time
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinSecondTick));

        // Assert: the ethereum price stands, the stale bitcoin tick did not win
        rig.Context.Current.LastPrice.Should().Be(EthereumFirstTick);
    }

    #endregion
}
