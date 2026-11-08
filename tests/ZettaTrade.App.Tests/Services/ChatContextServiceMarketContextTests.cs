namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The market-moment facts the chat context follows beside the workspace
/// walk: the ticker's 24h change and price ride the same captured-symbol
/// guard (a late frame of an old feed cannot overwrite the new market), the
/// book keeps the top levels per side with the withdrawal fact, the venue
/// classification is the decided fact the source owns, and the enabled
/// indicator setup rides the persisted settings (restored once, re-applied
/// live). Every absent part stays absent — the snapshot never substitutes a
/// fact it does not own.
/// </summary>
public sealed class ChatContextServiceMarketContextTests
{
    #region Constants

    /// <summary>The staged bitcoin tick price.</summary>
    private const decimal BitcoinPrice = 50_000m;

    /// <summary>The staged 24h change percent one tick carries.</summary>
    private const decimal StagedChange24h = 0.4m;

    /// <summary>The canonical paper venue id (the infrastructure adapter's
    /// ExchangeId; the service only performs the comparison).</summary>
    private const string PaperVenueId = "paper";

    /// <summary>One percent of the default indicator JSON shape.</summary>
    private const string DefaultSetupJson = UiSettingsKeys.DefaultChartIndicators;

    #endregion

    #region Fixtures

    /// <summary>A staged ticker carrying both price and change.</summary>
    private static Ticker StagedTicker(Symbol symbol, decimal lastPrice, decimal change) => new(
        symbol,
        lastPrice,
        Bid: lastPrice - 1m,
        Ask: lastPrice + 1m,
        Volume24h: 12m,
        PriceChange24hPercent: change,
        Timestamp: DateTimeOffset.UtcNow);

    /// <summary>A staged book: <paramref name="levels"/> levels per side.</summary>
    private static OrderBook StagedBook(Symbol symbol, int levels) => new()
    {
        Symbol = symbol,
        Bids = [.. Enumerable.Range(0, levels).Select(index => new PriceLevel(49_999m - index, 1m + index))],
        Asks = [.. Enumerable.Range(0, levels).Select(index => new PriceLevel(50_001m + index, 2m + index))],
        Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>The extended rig: the context and every market-moment source
    /// a test may drive (the ticker and book feeds are exposed subjects).</summary>
    private sealed record MomentRig(
        ChatContextService Context,
        IMarketDataAdapter Adapter,
        ITradingAdapter? Venue,
        ISettingsService? Settings,
        Subject<SettingsChange> Changes,
        Subject<Symbol> Symbols,
        Subject<TimeFrame> TimeFrames,
        List<Subject<Ticker>> Tickers,
        List<Subject<OrderBook>> Books);

    private static MomentRig CreateRig(ITradingAdapter? venue = null, ISettingsService? settings = null)
    {
        var changes = new Subject<SettingsChange>();
        if (settings is not null)
        {
            settings.Changes.Returns(changes);
        }

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Name.Returns("Test Exchange");
        var tickers = new List<Subject<Ticker>>();
        var books = new List<Subject<OrderBook>>();
        adapter.SubscribeTicker(Arg.Any<Symbol>()).Returns(call =>
        {
            var subject = new Subject<Ticker>();
            tickers.Add(subject);

            return subject;
        });
        adapter.SubscribeOrderBook(Arg.Any<Symbol>(), Arg.Any<int>()).Returns(call =>
        {
            var subject = new Subject<OrderBook>();
            books.Add(subject);

            return subject;
        });

        var symbols = new Subject<Symbol>();
        var timeFrames = new Subject<TimeFrame>();
        var context = new ChatContextService(
            adapter,
            symbols,
            timeFrames,
            NullLogger<ChatContextService>.Instance,
            venue,
            settings);

        return new MomentRig(
            context,
            adapter,
            venue,
            settings,
            changes,
            symbols,
            timeFrames,
            tickers,
            books);
    }

    #endregion

    #region Ticker Facts

    /// <summary>A tick carries its 24h change beside its price: both land in
    /// the snapshot under the captured-symbol guard.</summary>
    [Fact]
    public void Current_AfterATick_CarriesBothTickerFacts()
    {
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);

        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinPrice, StagedChange24h));

        rig.Context.Current.LastPrice.Should().Be(BitcoinPrice);
        rig.Context.Current.PriceChange24hPercent.Should().Be(StagedChange24h);
    }

    /// <summary>A symbol-less workspace renders no market fact even if the
    /// parked time frame stood (the per-part honesty of the snapshot).</summary>
    [Fact]
    public void Current_WithoutASymbol_RendersNoMarketFact()
    {
        var rig = CreateRig();
        rig.TimeFrames.OnNext(TimeFrame.H1);

        rig.Context.Current.LastPrice.Should().BeNull();
        rig.Context.Current.PriceChange24hPercent.Should().BeNull();
        rig.Context.Current.BestBids.Should().BeNull();
        rig.Context.Current.BestAsks.Should().BeNull();
    }

    /// <summary>A market switch blanks the stale facts immediately: the new
    /// market renders nothing until its own frames arrive.</summary>
    [Fact]
    public void Current_TheMarketSwitch_BlanksTheStaleFacts()
    {
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinPrice, StagedChange24h));
        rig.Books[0].OnNext(StagedBook(bitcoin, 3));

        rig.Symbols.OnNext(Symbol.Parse("ETH/BTC"));

        rig.Context.Current.LastPrice.Should().BeNull("the new market has no facts of its own yet");
        rig.Context.Current.PriceChange24hPercent.Should().BeNull();
        rig.Context.Current.BestBids.Should().BeNull();
    }

    #endregion

    #region Book Facts

    /// <summary>The book facts keep the top levels per side only, best
    /// first — the snapshot holds the shape the moment renders.</summary>
    [Fact]
    public void Current_AfterABookFrame_KeepsTheTopLevelsPerSide()
    {
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);

        rig.Books[0].OnNext(StagedBook(bitcoin, 5));

        rig.Context.Current.BestBids.Should().HaveCount(3);
        rig.Context.Current.BestBids![0].Price.Should().Be(49_999m);
        rig.Context.Current.BestAsks.Should().HaveCount(3);
        rig.Context.Current.BestAsks![0].Price.Should().Be(50_001m);
    }

    /// <summary>A book frame whose sides carried empty renders the
    /// withdrawal fact: the explicit empty list, never stored leftovers.</summary>
    [Fact]
    public void Current_AnEmptySidedBookFrame_RendersTheWithdrawalFact()
    {
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);
        rig.Books[0].OnNext(StagedBook(bitcoin, 3));

        rig.Books[0].OnNext(StagedBook(bitcoin, 0));

        rig.Context.Current.BestBids.Should().NotBeNull().And.BeEmpty();
        rig.Context.Current.BestAsks.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region Venue Tag

    /// <summary>The venue renders the decided classification (the service
    /// performs the comparison; it never guesses a tag).</summary>
    [Theory]
    [InlineData(PaperVenueId, true)]
    [InlineData("binance", false)]
    public void Current_TheTradingVenue_CarriesTheDecidedTag(string venueId, bool expected)
    {
        var venue = Substitute.For<ITradingAdapter>();
        venue.Id.Returns(venueId);
        var rig = CreateRig(venue: venue);

        rig.Context.Current.IsPaperVenue.Should().Be(expected);
        rig.Context.Current.VenueId.Should().Be(venueId);
    }

    #endregion

    #region Indicator Setup

    /// <summary>The initialized setup read: the persisted rows' enabled
    /// entries become the snapshot's setup facts (disabled stay out).</summary>
    [Fact]
    public async Task InitializeAsync_ThePersistedSetup_LandsAsEnabledRowsOnly()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(DefaultSetupJson);
        var rig = CreateRig(settings: settings);

        await rig.Context.InitializeAsync();

        rig.Context.Current.Indicators.Should().ContainSingle()
            .Which.Should().Be(new ChatIndicatorRow("SMA", 20));
    }

    /// <summary>A settings change re-applies live: the writer's new JSON
    /// replaces the rows without a restart.</summary>
    [Fact]
    public async Task Changes_TheIndicatorSetupWriter_ReAppliesLive()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(DefaultSetupJson);
        var rig = CreateRig(settings: settings);
        await rig.Context.InitializeAsync();

        rig.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            DefaultSetupJson,
            """[{"name":"EMA","enabled":true,"period":9},{"name":"RSI","enabled":false,"period":14}]"""));

        rig.Context.Current.Indicators.Should().ContainSingle()
            .Which.Should().Be(new ChatIndicatorRow("EMA", 9), "the disabled row stays out, the enabled one re-applies");
    }

    /// <summary>The rows stay absent until a read ever happened (null is
    /// "unknown", not "none").</summary>
    [Fact]
    public void Current_BeforeAnySetupRead_LeavesTheRowsAbsent()
    {
        var rig = CreateRig(settings: Substitute.For<ISettingsService>());

        rig.Context.Current.Indicators.Should().BeNull();
    }

    #endregion

    #region Projection

    /// <summary>The workspace-to-moment projection is a field-for-field
    /// pass: a fact stays a fact, a null stays a null.</summary>
    [Fact]
    public void ToMarketMoment_TheProjection_CarriesEachFactOneToOne()
    {
        var rig = CreateRig();
        var bitcoin = Symbol.Parse("BTC/USDT");
        rig.Symbols.OnNext(bitcoin);
        rig.Tickers[0].OnNext(StagedTicker(bitcoin, BitcoinPrice, StagedChange24h));
        rig.Books[0].OnNext(StagedBook(bitcoin, 3));

        var moment = rig.Context.Current.ToMarketMoment();

        moment.LastPrice.Should().Be(BitcoinPrice);
        moment.PriceChange24hPercent.Should().Be(StagedChange24h);
        moment.Bids.Should().HaveCount(3);
        moment.Asks.Should().HaveCount(3);
    }

    [Fact]
    public void ToMarketMoment_TheAbsentParts_StayAbsent()
    {
        var moment = CreateRig().Context.Current.ToMarketMoment();

        moment.LastPrice.Should().BeNull();
        moment.Bids.Should().BeNull();
        moment.IsPaperVenue.Should().BeNull();
        moment.Indicators.Should().BeNull();
    }

    #endregion
}
