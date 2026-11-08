namespace ZettaTrade.Infrastructure.Tests.Fees;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Exchange;
using Infrastructure.Fees;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Infrastructure.Tests.Exchange;

/// <summary>
/// Schedule-selection goldens for the per-adapter fee registry (T-080):
/// without configuration the shipped paper schedule answers the paper
/// adapter and UNKNOWN adapters stay unknown (null — the dash surface);
/// a configured entry answers its adapter (an explicit paper entry replaces
/// the shipped default); invalid entries are never charged; and a
/// Fees-section settings change re-reads the section and swaps the snapshot
/// live (the reactive settings canon).
/// </summary>
public sealed class FeeScheduleServiceTests
{
    #region Test Constants

    /// <summary>Bounded wait for the change-driven snapshot swap.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>The schedule a configured (non-paper) adapter carries in the goldens.</summary>
    private static readonly FeeSchedule BinanceSchedule = new(MakerRateBps: 8m, TakerRateBps: 12m, MinimumFee: 1m);

    /// <summary>The shipped simulation schedule the paper adapter prices with.</summary>
    private static readonly FeeSchedule ShippedPaperSchedule =
        new(FeeScheduleService.PaperMakerRateBps, FeeScheduleService.PaperTakerRateBps, FeeScheduleService.PaperMinimumFee);

    /// <summary>An explicit paper entry that replaces the shipped default.</summary>
    private static readonly FeeSchedule PaperOverrideSchedule =
        new(MakerRateBps: 50m, TakerRateBps: 60m, MinimumFee: 5m);

    #endregion

    #region Selection

    [Fact]
    public async Task GetSchedule_WithoutConfiguration_ShipsThePaperDefault_AndUnknownAdaptersStayUnknown()
    {
        await using var engine = await CreateAsync(Substitute.For<ISettingsService>());

        engine.GetSchedule(PaperTradingAdapter.ExchangeId).Should().Be(ShippedPaperSchedule);
        engine.GetSchedule("BINANCE").Should().BeNull("a schedule-less adapter is the honest unknown, not a zero");
    }

    [Fact]
    public async Task GetSchedule_ConfiguredEntries_AnswerTheirAdapterIds_CaseInsensitively()
    {
        var settings = Substitute.For<ISettingsService>();
        ConfigureRead(settings, new Dictionary<string, FeeSchedule>(StringComparer.OrdinalIgnoreCase)
        {
            ["binance"] = BinanceSchedule,
        });

        await using var engine = await CreateAsync(settings);

        engine.GetSchedule("binance").Should().Be(BinanceSchedule);
        engine.GetSchedule("BINANCE").Should().Be(BinanceSchedule, "the registry keys match case-insensitively");
        engine.GetSchedule(PaperTradingAdapter.ExchangeId).Should().Be(ShippedPaperSchedule, "the shipped default covers the paper adapter while it carries no explicit entry");
    }

    [Fact]
    public async Task GetSchedule_AnExplicitPaperEntry_ReplacesTheShippedDefault()
    {
        var settings = Substitute.For<ISettingsService>();
        ConfigureRead(settings, new Dictionary<string, FeeSchedule>(StringComparer.OrdinalIgnoreCase)
        {
            [PaperTradingAdapter.ExchangeId] = PaperOverrideSchedule,
        });

        await using var engine = await CreateAsync(settings);

        engine.GetSchedule(PaperTradingAdapter.ExchangeId).Should().Be(PaperOverrideSchedule);
    }

    [Fact]
    public async Task GetSchedule_InvalidEntries_NeverCharge()
    {
        var settings = Substitute.For<ISettingsService>();
        ConfigureRead(settings, new Dictionary<string, FeeSchedule>(StringComparer.OrdinalIgnoreCase)
        {
            // A negative rate is a configuration error: the entry drops.
            ["binance"] = BinanceSchedule with { TakerRateBps = -2m },
            // A blank adapter id cannot resolve to anything.
            ["   "] = PaperOverrideSchedule,
        });

        await using var engine = await CreateAsync(settings);

        engine.GetSchedule("binance").Should().BeNull("a dropped entry is an absence, never a charge");
        engine.GetSchedule(PaperTradingAdapter.ExchangeId).Should().Be(ShippedPaperSchedule, "the shipped default survives invalid company");
    }

    [Fact]
    public async Task GetSchedule_NullOrEmptyConfiguration_FallsBackToTheShippedState()
    {
        var settings = Substitute.For<ISettingsService>();
        ConfigureRead(settings, read: null);

        await using var engine = await CreateAsync(settings);

        engine.GetSchedule(PaperTradingAdapter.ExchangeId).Should().Be(ShippedPaperSchedule);
        engine.GetSchedule("binance").Should().BeNull();
    }

    #endregion

    #region Reactive settings

    [Fact]
    public async Task InitializeAsync_AFeesSectionChange_ReReadsAndSwapsTheSnapshot()
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        ConfigureRead(settings, read: null);

        await using var engine = new FeeScheduleService(settings, NullLogger<FeeScheduleService>.Instance);
        await engine.InitializeAsync();
        var present = new Dictionary<string, FeeSchedule>(StringComparer.OrdinalIgnoreCase)
        {
            [PaperTradingAdapter.ExchangeId] = PaperOverrideSchedule,
        };

        ConfigureRead(settings, present);
        changes.OnNext(new SettingsChange(FeeScheduleService.SettingsSection, FeeScheduleService.SettingsKey, null, present));

        await TestPoll.PollUntilAsync(
            () => engine.GetSchedule(PaperTradingAdapter.ExchangeId) == PaperOverrideSchedule,
            EventWait);

        // An unrelated section never re-reads the fees.
        ConfigureRead(settings, read: null);
        changes.OnNext(new SettingsChange("chart", "symbol", null, "ETH/USDT"));
        engine.GetSchedule(PaperTradingAdapter.ExchangeId)
            .Should().Be(PaperOverrideSchedule, "only the Fees section drives the snapshot");
    }

    #endregion

    #region Helpers

    /// <summary>Create an initialized engine whose sole dependency is the given settings substitute.</summary>
    private static async Task<FeeScheduleService> CreateAsync(ISettingsService settings)
    {
        // An empty change stream: these tests configure the snapshot read
        // directly, the reactive reload has its own test.
        settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());
        var engine = new FeeScheduleService(settings, NullLogger<FeeScheduleService>.Instance);
        await engine.InitializeAsync();
        return engine;
    }

    /// <summary>Point the settings read at the given snapshot (null = nothing configured).</summary>
    private static void ConfigureRead(
        ISettingsService settings,
        IReadOnlyDictionary<string, FeeSchedule>? read) =>
        settings
            .GetAsync<IReadOnlyDictionary<string, FeeSchedule>>(
                Arg.Is(FeeScheduleService.SettingsSection),
                Arg.Is(FeeScheduleService.SettingsKey),
                Arg.Any<IReadOnlyDictionary<string, FeeSchedule>>())
            .Returns(read!);

    #endregion
}
