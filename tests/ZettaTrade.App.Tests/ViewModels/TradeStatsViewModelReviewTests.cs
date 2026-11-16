namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The trade-review route's pins (T-255): the row action composes the
/// structured prompt through the pure composer and hands it to the view
/// in one interaction — nothing else moves (no journal write, no
/// trading call: the stats VM's substitutes stay silent). Strictly
/// read-only toward the trading surface, the output lives in the chat
/// only.
/// </summary>
public sealed class TradeStatsViewModelReviewTests
{
    [Fact]
    public async Task ReviewTradeCommand_Executed_HandsTheComposedPromptToTheView()
    {
        // Arrange
        var stats = CreateStats();
        var captured = new List<string>();
        using (stats.ReviewTradeRequested.RegisterHandler(context =>
        {
            captured.Add(context.Input);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        }))
        {
            var row = ReviewRow();

            // Act
            await stats.ReviewTradeCommand.Execute(row);

            // Assert
            captured.Should().HaveCount(1);
            captured[0].Should().Be(
                TradeReviewPromptComposer.Compose(row),
                "the view receives the composed prompt, never the raw row");
        }
    }

    [Fact]
    public async Task ReviewTradeCommand_Executed_WritesNothingAnywhere()
    {
        // Arrange
        var store = Substitute.For<IOrderHistoryStore>();
        var stats = CreateStats(store);
        using (stats.ReviewTradeRequested.RegisterHandler(context =>
        {
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        }))
        {
            // Act
            await stats.ReviewTradeCommand.Execute(ReviewRow());

            // Assert
            _ = store.DidNotReceiveWithAnyArgs().SetTradeMetaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        }
    }

    private static TradeStatsViewModel CreateStats(IOrderHistoryStore? store = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        var exchanges = new ActiveExchangeService(
            [Substitute.For<IMarketDataAdapter>()],
            settings,
            NullLogger<ActiveExchangeService>.Instance);

        return new TradeStatsViewModel(
            Substitute.For<IOrderStatsQuery>(),
            Substitute.For<ITradingAdapter>(),
            Substitute.For<ICsvExportService>(),
            Substitute.For<IStatementExportService>(),
            store ?? Substitute.For<IOrderHistoryStore>(),
            ChatTests.Localization(),
            new TradeExcursionReader(Substitute.For<ICandleCache>()),
            exchanges,
            NullLogger<TradeStatsViewModel>.Instance);
    }

    private static TradeRealizedRowViewModel ReviewRow() => new()
    {
        ExchangeId = "binance",
        OrderId = "ord-1",
        ClosedAt = "2026-11-16 10:00",
        Exchange = "Binance",
        Symbol = "BTC/USDT",
        Side = "Buy",
        IsBuySide = true,
        IsSellSide = false,
        Quantity = "0.5",
        EntryPrice = "65,000.00",
        ExitPrice = "65,500.00",
        Pnl = "+250.00",
        IsWinning = true,
        IsLosing = false,
        Mae = "-120.00",
        Mfe = "+310.00",
        Note = null,
        Tags = null,
    };
}
