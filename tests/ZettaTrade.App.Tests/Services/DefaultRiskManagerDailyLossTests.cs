namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The daily loss governor's behavior AT the order gate (T-189): a crossed
/// threshold blocks new orders with an honest reason naming the day loss
/// against the limit; a reduce-only request is exempt before any read at
/// all (the ledger file being deleted proves the exemption — a read would
/// fault); the off state is bit-identical to the ungoverned past (no engine
/// read either); a blocked LARGE order never reaches the confirmation
/// threshold (the gate rides before it); a quoteless order passes
/// unattributed; and an engine fault lands fail-closed as a rejection,
/// never an approval on unknown state.
/// </summary>
public sealed class DefaultRiskManagerDailyLossTests
{
    #region Test Constants

    /// <summary>
    /// The fake clock's standing moment: the CURRENT zone's local noon —
    /// derived once per run, so every seeded stamp sits inside the local
    /// day whatever zone the host runs in.
    /// </summary>
    private static readonly DateTimeOffset BaseTime = CreateLocalNoon();

    private static DateTimeOffset CreateLocalNoon()
    {
        var systemNow = TimeProvider.System.GetLocalNow();
        return new(systemNow.Year, systemNow.Month, systemNow.Day, 12, 0, 0, systemNow.Offset);
    }

    #endregion Test Constants

    #region The Block

    [Fact]
    public async Task CheckOrderAsync_AtTheLimit_RejectsWithTheHonestReason()
    {
        await WithGovernedManagerAsync(async (manager, harness) =>
        {
            await harness.SeedLossAsync(-1000m);

            // 0.1 BTC at 50_000 = 5_000 USD: inside the confirmation band's
            // free zone, so the ONLY thing that can reject it is the loss
            // gate.
            var result = await manager.CheckOrderAsync(LimitOrder(0.1m, 50_000m));

            result.IsApproved.Should().BeFalse("the day's net loss reached the limit");
            result.RequiresConfirmation.Should().BeFalse("a blocked order reaches no dialog");
            result.Reason.Should().Contain("Daily loss limit reached")
                .And.Contain("-1000.00", "the reason names the honest day loss")
                .And.Contain("1000.00 USDT", "the reason names the limit and its currency")
                .And.Contain("closing orders stay allowed", "the block is protective, never a trap");
        });
    }

    [Fact]
    public async Task CheckOrderAsync_BlockedLargeOrder_NeverReachesTheConfirmation()
    {
        await WithGovernedManagerAsync(async (manager, harness) =>
        {
            await harness.SeedLossAsync(-1000m);

            // 0.3 BTC at 50_000 = 15_000 USD: above the confirmation
            // threshold — ungoverned this same request approves WITH a
            // confirmation (the legacy battery pins that). The loss gate
            // must ride BEFORE it: a blocked order never reaches a dialog.
            var result = await manager.CheckOrderAsync(LimitOrder(0.3m, 50_000m));

            result.IsApproved.Should().BeFalse();
            result.RequiresConfirmation.Should().BeFalse(
                "the loss gate rejects before the confirmation threshold is ever consulted");
        });
    }

    #endregion The Block

    #region The Exemptions

    [Fact]
    public async Task CheckOrderAsync_ReduceOnly_IsExemptBeforeAnyRead()
    {
        await WithGovernedManagerAsync(async (manager, harness) =>
        {
            await harness.SeedLossAsync(-1000m);
            File.Delete(harness.PnlFile); // the ledger is gone: a read would fault

            var result = await manager.CheckOrderAsync(
                LimitOrder(0.1m, 50_000m) with { ReduceOnly = true });

            result.IsApproved.Should().BeTrue(
                "closing exposure never rides the loss gate — the exemption precedes any read");
            result.RequiresConfirmation.Should().BeFalse();
        });
    }

    [Fact]
    public async Task CheckOrderAsync_Off_IsBitIdenticalAndPerformsNoRead()
    {
        await WithGovernedManagerAsync(
            async (manager, harness) =>
            {
                File.Delete(harness.PnlFile); // the ledger is gone: a read would fault

                var result = await manager.CheckOrderAsync(LimitOrder(0.1m, 50_000m));

                result.IsApproved.Should().BeTrue(
                    "the off governor changes nothing — not even a ledger read");
                result.RequiresConfirmation.Should().BeFalse();
            },
            enabled: false);
    }

    [Fact]
    public async Task CheckOrderAsync_QuotelessOrder_PassesUnattributed()
    {
        await WithGovernorHarnessAsync(async harness =>
        {
            // Arm the governor explicitly (an unconfigured substitute would
            // answer disabled and the fact would pass via the off branch):
            // the quoteless order must pass WHILE the governor is on.
            harness.Settings.GetAsync(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled, false)
                .Returns(true);
            harness.Settings.GetAsync(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossLimitAmount,
                    RiskSettingsKeys.DailyLossLimitAmountValue)
                .Returns(1000m);

            // A stock symbol carries no quote asset: its loss lands in the
            // empty-string bucket, which may mix several currencies — the
            // gate passes the order rather than blocking on a fabricated
            // attribution.
            await harness.Engine.IngestFillAsync(
                harness.Fill("aapl-open", OrderSide.Buy, "AAPL", 1m, 1100m),
                commission: null);
            await harness.Engine.IngestFillAsync(
                harness.Fill("aapl-close", OrderSide.Sell, "AAPL", 1m, 100m, BaseTime.AddMinutes(1)),
                commission: null);

            var manager = new DefaultRiskManager(
                NullLogger<DefaultRiskManager>.Instance,
                harness.CreateService());

            var result = await manager.CheckOrderAsync(LimitOrder(10m, 100m, "AAPL"));

            result.IsApproved.Should().BeTrue("the governor never blocks on an unattributable bucket");
        });
    }

    #endregion The Exemptions

    #region The Fail-Closed Path

    [Fact]
    public async Task CheckOrderAsync_EngineFault_RejectsFailClosed()
    {
        await WithGovernedManagerAsync(async (manager, harness) =>
        {
            File.Delete(harness.PnlFile);

            var result = await manager.CheckOrderAsync(LimitOrder(0.1m, 50_000m));

            result.IsApproved.Should().BeFalse(
                "an unknown loss state never approves an order");
            result.RequiresConfirmation.Should().BeFalse();
            result.Reason.Should().Contain("Risk check error")
                .And.Contain("no such table", "the honest fault text lands on the rejection");
        });
    }

    #endregion The Fail-Closed Path

    #region Helpers

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>The whole harness one fact body rides: the clock, the settings stub, the engine and its temp files.</summary>
    private sealed class GovernorHarness
    {
        public required FakeClock Clock { get; init; }

        public required ISettingsService Settings { get; init; }

        public required ITradingAdapter Surface { get; init; }

        public required PnlAccountingService Engine { get; init; }

        public required string JournalFile { get; init; }

        public required string PnlFile { get; init; }

        public DailyLossLimitService CreateService() =>
            new(Surface, Engine, Settings, Clock, NullLogger<DailyLossLimitService>.Instance);

        public Order Fill(
            string id,
            OrderSide side,
            string symbol,
            decimal quantity,
            decimal price,
            DateTimeOffset? createdAt = null,
            string exchange = "paper") => new()
            {
                Id = id,
                ExchangeId = exchange,
                Symbol = Symbol.Parse(symbol),
                Side = side,
                Type = OrderType.Market,
                Quantity = new Quantity(quantity),
                Price = new Price(price),
                Status = OrderStatus.Filled,
                TimeInForce = TimeInForce.GTC,
                CreatedAt = createdAt ?? BaseTime,
                FilledQuantity = new Quantity(quantity),
            };

        /// <summary>
        /// Seed one net realized result on the paper lane: open one unit at
        /// 1100, close it one minute later at the price the loss names — the
        /// result equals the loss exactly (no commission).
        /// </summary>
        public async Task SeedLossAsync(decimal loss)
        {
            await Engine.IngestFillAsync(
                Fill("gate-seed-open", OrderSide.Buy, "BTC/USDT", 1m, 1100m),
                commission: null);
            await Engine.IngestFillAsync(
                Fill("gate-seed-close", OrderSide.Sell, "BTC/USDT", 1m, 1100m + loss, BaseTime.AddMinutes(1)),
                commission: null);
        }
    }

    private static async Task WithGovernorHarnessAsync(Func<GovernorHarness, Task> testBody)
    {
        var journalFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-gate-journal-{Guid.NewGuid():N}.db");
        var pnlFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-gate-pnl-{Guid.NewGuid():N}.db");
        var journal = new SqliteOrderHistoryStore(
            new DbContextOptionsBuilder<OrderHistoryDbContext>()
                .UseSqlite($"Data Source={journalFile};Pooling=false")
                .Options,
            NullLogger<SqliteOrderHistoryStore>.Instance);

        try
        {
            await journal.InitializeAsync();
            var harness = new GovernorHarness
            {
                Clock = new FakeClock(BaseTime),
                Settings = Substitute.For<ISettingsService>(),
                Surface = CreateSurface("paper"),
                Engine = new PnlAccountingService(
                    journal,
                    new DbContextOptionsBuilder<PnlDbContext>()
                        .UseSqlite($"Data Source={pnlFile};Pooling=false")
                        .Options,
                    NullLogger<PnlAccountingService>.Instance),
                JournalFile = journalFile,
                PnlFile = pnlFile,
            };
            await harness.Engine.InitializeAsync();
            await testBody(harness);
            await harness.Engine.DisposeAsync();
        }
        finally
        {
            foreach (var file in new[] { journalFile, pnlFile })
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// One governed manager over the real governor (the SQLite ledger, the
    /// stubbed settings, the paper lane): the harness arms the switch and
    /// the threshold unless the fact asks for the off state.
    /// </summary>
    private static Task WithGovernedManagerAsync(
        Func<DefaultRiskManager, GovernorHarness, Task> testBody,
        bool enabled = true) =>
        WithGovernorHarnessAsync(async harness =>
        {
            harness.Settings.GetAsync(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled, false)
                .Returns(enabled);
            harness.Settings.GetAsync(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossLimitAmount,
                    RiskSettingsKeys.DailyLossLimitAmountValue)
                .Returns(1000m);
            harness.Settings.GetAsync<string?>(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossResetAt,
                    null)
                .Returns((string?)null);

            var manager = new DefaultRiskManager(
                NullLogger<DefaultRiskManager>.Instance,
                harness.CreateService());

            await testBody(manager, harness);
        });

    private static ITradingAdapter CreateSurface(string id)
    {
        var surface = Substitute.For<ITradingAdapter>();
        surface.Id.Returns(id);
        return surface;
    }

    private static OrderRequest LimitOrder(decimal quantity, decimal price, string symbol = "BTC/USDT") => new()
    {
        Symbol = Symbol.Parse(symbol),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
    };

    #endregion Helpers
}
