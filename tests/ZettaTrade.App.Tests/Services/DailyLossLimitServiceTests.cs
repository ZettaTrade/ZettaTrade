namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
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
/// The daily loss governor's state facts (T-189): the threshold math
/// compares the plain window sum of the realized rows (each already net of
/// its commission — no second subtraction), buckets never mix quote
/// currencies, lanes never mix facts, a quoteless order is passed
/// unattributed, the off state performs no engine read at all, and the
/// block lifts at the day rollover or by the operator's reset (a nonsense
/// or unparsable marker is never substituted with a fabricated moment).
/// </summary>
public sealed class DailyLossLimitServiceTests
{
    #region Test Constants

    /// <summary>
    /// The fake clock's standing moment: the CURRENT zone's local noon —
    /// derived from the system clock's zone once per run, so every seeded
    /// stamp sits inside the local day whatever zone the host runs in.
    /// </summary>
    private static readonly DateTimeOffset BaseTime = CreateLocalNoon();

    private static DateTimeOffset CreateLocalNoon()
    {
        var systemNow = TimeProvider.System.GetLocalNow();
        return new(systemNow.Year, systemNow.Month, systemNow.Day, 12, 0, 0, systemNow.Offset);
    }

    #endregion Test Constants

    #region The Threshold Math

    [Fact]
    public async Task ReadStateAsync_AtTheLimit_Blocks()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.Enabled.Should().BeTrue();
                state.IsBlocked.Should().BeTrue(
                    "the day's net realized result reached the limit — a limit is inclusive");
                state.DayLoss.Should().Be(-1000m);
                state.Limit.Should().Be(1000m);
                state.Quote.Should().Be("USDT");
                state.ExchangeId.Should().Be("paper");
            },
            seed: async g => await g.SeedLossAsync(-1000m));
    }

    [Fact]
    public async Task ReadStateAsync_BelowTheLimit_DoesNotBlock()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.IsBlocked.Should().BeFalse("the loss never crossed the threshold");
                state.DayLoss.Should().Be(-999.99m);
            },
            seed: async g => await g.SeedLossAsync(-999.99m));
    }

    [Fact]
    public async Task ReadStateAsync_Profit_DoesNotBlock()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.IsBlocked.Should().BeFalse("a profitable day is not a losing day");
                state.DayLoss.Should().Be(200m);
            },
            seed: async g => await g.SeedLossAsync(200m));
    }

    [Fact]
    public async Task ReadStateAsync_CommissionStaysNet_NoSecondSubtraction()
    {
        // The rows store amounts already net of their commissions: an open
        // fill realized −10 (a pure opening cost) and a closing fill
        // realized −1000 gross with a 5 commission → −1015 net. The
        // governor sums the stored amounts — subtracting the commissions
        // again would double-count the fees.
        await WithGovernorAsync(async (service, harness) =>
        {
            await harness.Engine.IngestFillAsync(
                harness.Fill("f1", OrderSide.Buy, "BTC/USDT", 1m, 1100m),
                commission: 10m);
            await harness.Engine.IngestFillAsync(
                harness.Fill("f2", OrderSide.Sell, "BTC/USDT", 1m, 100m, BaseTime.AddMinutes(1)),
                commission: 5m);

            var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

            state.DayLoss.Should().Be(-1015m);
            state.IsBlocked.Should().BeTrue();
        });
    }

    #endregion The Threshold Math

    #region Attribution

    [Fact]
    public async Task ReadStateAsync_BucketsNeverMixQuoteCurrencies()
    {
        // BTC/USDT and ETH/BTC carry DIFFERENT quote currencies: the seeds
        // land in the USDT and BTC buckets respectively, and no read ever
        // sums across them.
        await WithGovernorAsync(async (service, harness) =>
        {
            await harness.SeedLossAsync(-1000m);
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-eth", OrderSide.Buy, "ETH/BTC", 10m, 100m, BaseTime.AddMinutes(2)),
                commission: null);
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-eth2", OrderSide.Sell, "ETH/BTC", 10m, 150m, BaseTime.AddMinutes(3)),
                commission: null);

            var usdtState = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));
            var btcBucketState = await service.ReadStateAsync(Symbol.Parse("ETH/BTC"));

            usdtState.IsBlocked.Should().BeTrue("the USDT sum itself crossed the limit");
            usdtState.DayLoss.Should().Be(-1000m);
            btcBucketState.IsBlocked.Should().BeFalse("another currency's loss never blocks this order");
            btcBucketState.DayLoss.Should().Be(500m);
        });
    }

    [Fact]
    public async Task ReadStateAsync_QuotelessOrder_PassesUnattributed()
    {
        // A stock symbol carries no quote asset: the empty-string bucket may
        // mix several currencies, so the governor passes the order and
        // honestly reports what it read without blocking on it.
        await WithGovernorAsync(async (service, harness) =>
        {
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-aapl", OrderSide.Buy, "AAPL", 1m, 1100m, BaseTime.AddMinutes(2)),
                commission: null);
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-aapl2", OrderSide.Sell, "AAPL", 1m, 100m, BaseTime.AddMinutes(3)),
                commission: null);

            var state = await service.ReadStateAsync(Symbol.Parse("AAPL"));

            state.Quote.Should().BeEmpty();
            state.DayLoss.Should().Be(-1000m, "the empty-string bucket's loss is honestly read");
            state.IsBlocked.Should().BeFalse(
                "the governor never blocks on an unattributable bucket");
        });
    }

    [Fact]
    public async Task ReadStateAsync_OtherLanesFacts_NeverBlockThisLane()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.DayLoss.Should().Be(0m, "the loss lives on the other lane");
                state.IsBlocked.Should().BeFalse();
            },
            lane: "bybit",
            seed: async g => await g.SeedLossAsync(-1000m));
    }

    #endregion Attribution

    #region The Off State

    [Fact]
    public async Task ReadStateAsync_Off_PerformsNoEngineReadAndBlocksNothing()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.Enabled.Should().BeFalse();
                state.IsBlocked.Should().BeFalse();
                state.DayLoss.Should().Be(
                    0m,
                    "zero despite the seeded loss — the engine was never read");
            },
            enabled: false,
            seed: async g => await g.SeedLossAsync(-1000m));
    }

    [Fact]
    public async Task ReadStateAsync_NonsenseThreshold_StaysIdle()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.Enabled.Should().BeTrue();
                state.IsBlocked.Should().BeFalse(
                    "a non-positive limit can never be crossed honestly — the governor stays idle");
            },
            limit: 0m,
            seed: async g => await g.SeedLossAsync(-1000m));
    }

    #endregion The Off State

    #region The Lift Terms

    [Fact]
    public async Task ReadStateAsync_DayRollover_ReAnchorsTheWindow()
    {
        await WithGovernorAsync(async (service, harness) =>
        {
            await harness.SeedLossAsync(-1000m);
            (await service.ReadStateAsync(Symbol.Parse("BTC/USDT"))).IsBlocked.Should().BeTrue();

            harness.Clock.Advance(TimeSpan.FromDays(1));

            var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));
            state.DayLoss.Should().Be(0m, "yesterday's loss belongs to yesterday's window");
            state.IsBlocked.Should().BeFalse();
        });
    }

    [Fact]
    public async Task Reset_WritesTheMarkerAndLiftsTheBlockForTheRestOfTheDay()
    {
        await WithGovernorAsync(async (service, harness) =>
        {
            // The loss realized two hours BEFORE the reset moment.
            await harness.SeedLossAsync(-1000m, createdAt: BaseTime.AddHours(-2));
            (await service.ReadStateAsync(Symbol.Parse("BTC/USDT"))).IsBlocked.Should().BeTrue();

            // Capture the written marker: the Arg.Do matcher is configured
            // BEFORE the act (inside a When specification) — configuring it
            // inside a Received() assertion after the call never fires.
            string? stored = null;
            harness.Settings.When(sub => sub.SetAsync(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossResetAt,
                    Arg.Do<string>(value => stored = value)))
                .Do(_ => { });

            await service.ResetAsync();

            DateTimeOffset.TryParse(stored, out _).Should().BeTrue(
                "the marker rides an ISO-8601 UTC timestamp (got: '{0}')", stored);

            // The substitute's store does not persist writes: from here the
            // marker read answers the captured write — what a real settings
            // store hands back after the reset.
            harness.Settings.GetAsync<string?>(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossResetAt,
                    null)
                .Returns(stored);

            var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));
            state.IsBlocked.Should().BeFalse(
                "the window's start tightened to the marker — the pre-reset loss no longer blocks");

            // A loss AFTER the reset re-arms the block honestly.
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-post", OrderSide.Buy, "BTC/USDT", 1m, 1100m, BaseTime.AddMinutes(30)),
                commission: null);
            await harness.Engine.IngestFillAsync(
                harness.Fill("f-post2", OrderSide.Sell, "BTC/USDT", 1m, 100m, BaseTime.AddMinutes(31)),
                commission: null);

            (await service.ReadStateAsync(Symbol.Parse("BTC/USDT"))).IsBlocked.Should().BeTrue(
                "post-reset losses re-arm the block");
        });
    }

    [Fact]
    public async Task ReadStateAsync_UnparsableMarker_IsIgnoredAndTheBlockStands()
    {
        await WithGovernorAsync(
            async (service, _) =>
            {
                var state = await service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

                state.IsBlocked.Should().BeTrue(
                    "an unparsable marker is never substituted with a fabricated moment");
            },
            marker: "not-a-timestamp",
            seed: async g => await g.SeedLossAsync(-1000m));
    }

    [Fact]
    public async Task ReadStateAsync_EngineFault_PropagatesToTheCaller()
    {
        // The governor's read is the risk manager's fail-closed seam: the
        // fault PROPAGATES (the manager rejects on it) — the service never
        // swallows a ledger fault into a false "all clear".
        await WithGovernorHarnessAsync(async harness =>
        {
            // The read only reaches the engine when the governor is armed:
            // stub the switch and the threshold explicitly (an unconfigured
            // substitute would answer disabled and skip the ledger read).
            harness.Settings.GetAsync(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled, false)
                .Returns(true);
            harness.Settings.GetAsync(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossLimitAmount,
                    RiskSettingsKeys.DailyLossLimitAmountValue)
                .Returns(1000m);

            var service = harness.CreateService();
            File.Delete(harness.PnlFile);

            var act = () => service.ReadStateAsync(Symbol.Parse("BTC/USDT"));

            (await act.Should().ThrowAsync<SqliteException>())
                .Which.Message.Should().Contain("no such table");
        });
    }

    #endregion The Lift Terms

    #region Helpers

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
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
        /// 1100, close it one minute later at the price the loss names —
        /// the result equals the loss exactly (no commission). The anchor
        /// defaults to the standing moment; the reset fact seeds an earlier
        /// stamp so the pre-reset loss sits behind the marker.
        /// </summary>
        public async Task SeedLossAsync(
            decimal loss,
            DateTimeOffset? createdAt = null,
            string exchange = "paper")
        {
            var stamp = createdAt ?? BaseTime;
            await Engine.IngestFillAsync(
                Fill($"seed-{exchange}-open", OrderSide.Buy, "BTC/USDT", 1m, 1100m, exchange: exchange)
                    with
                { CreatedAt = stamp },
                commission: null);
            await Engine.IngestFillAsync(
                Fill($"seed-{exchange}-close", OrderSide.Sell, "BTC/USDT", 1m, 1100m + loss, exchange: exchange)
                    with
                { CreatedAt = stamp.AddMinutes(1) },
                commission: null);
        }
    }

    private static async Task WithGovernorHarnessAsync(Func<GovernorHarness, Task> testBody)
    {
        var journalFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-loss-journal-{Guid.NewGuid():N}.db");
        var pnlFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-loss-pnl-{Guid.NewGuid():N}.db");
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

    private static Task WithGovernorAsync(
        Func<DailyLossLimitService, GovernorHarness, Task> testBody,
        bool enabled = true,
        decimal limit = 1000m,
        string? marker = null,
        string lane = "paper",
        Func<GovernorHarness, Task>? seed = null) =>
        WithGovernorHarnessAsync(async harness =>
        {
            harness.Settings.GetAsync(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled, false)
                .Returns(enabled);
            harness.Settings.GetAsync(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossLimitAmount,
                    RiskSettingsKeys.DailyLossLimitAmountValue)
                .Returns(limit);
            harness.Settings.GetAsync<string?>(
                    RiskSettingsKeys.Section,
                    RiskSettingsKeys.DailyLossResetAt,
                    null)
                .Returns(marker);
            harness.Surface.Id.Returns(lane);

            if (seed is not null)
            {
                await seed(harness);
            }

            await testBody(harness.CreateService(), harness);
        });

    private static ITradingAdapter CreateSurface(string id)
    {
        var surface = Substitute.For<ITradingAdapter>();
        surface.Id.Returns(id);
        return surface;
    }

    #endregion Helpers
}
