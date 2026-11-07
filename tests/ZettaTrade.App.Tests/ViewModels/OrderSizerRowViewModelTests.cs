namespace ZettaTrade.App.Tests.ViewModels;

using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// Tests for the order ticket's quick-size row: the metadata gate (only
/// instruments with round-size metadata show the row), the balance presets
/// (sells ride captured base custody, buys convert captured quote custody
/// at the entry estimate) and the risk-percent mode (visible only for
/// order shapes that carry a stop distance). Every proposal is floored
/// onto the instrument's lot step and routed into the proposal stream the
/// ticket consumes; sizing itself never refetches — it rides the snapshot
/// captured once, only a deliberate refresh re-captures, and a merged
/// snapshot never outlives its instrument.
/// </summary>
public sealed class OrderSizerRowViewModelTests
{
    #region Fixtures

    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly Subject<SettingsChange> _settingsChanges = new();

    private static ILocalizationManager CreateLocalizer()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument.
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>()).Returns(call => $"[{call.ArgAt<string>(0)}]");
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
        return localization;
    }

    private ISettingsService CreateSettings(double storedPercent = SizerSettingsKeys.DefaultRiskPercentValue)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(storedPercent);
        settings.Changes.Returns(_settingsChanges);
        return settings;
    }

    private OrderSizerRowViewModel CreateRow(
        double storedPercent = SizerSettingsKeys.DefaultRiskPercentValue,
        IReadOnlyDictionary<string, Money>? balances = null)
    {
        if (balances is not null)
        {
            StubBalances(balances);
        }

        var service = new PositionSizingService(
            new PaperInstrumentMetadataProvider(),
            _trading,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PositionSizingService>>());
        return new OrderSizerRowViewModel(
            service,
            CreateSettings(storedPercent),
            CreateLocalizer(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderSizerRowViewModel>>());
    }

    private void StubBalances(IReadOnlyDictionary<string, Money> balances) =>
        _trading.GetBalancesAsync(Arg.Any<CancellationToken>()).Returns(balances);

    /// <summary>
    /// The newest-by-time reference close the row's entry estimate falls
    /// back to when the order type carries no explicit price.
    /// </summary>
    private void StubReferenceClose(decimal close) =>
        _trading.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([BuildReferenceBar(close)]);

    /// <summary>One H1 history bar at the given close — the reference rate's fixture.</summary>
    private static Bar BuildReferenceBar(decimal close) => new(
        DateTimeOffset.UtcNow,
        close,
        close,
        close,
        close,
        1m);

    /// <summary>
    /// Commit the symbol into the row and await the capture publish the
    /// deterministic seam emits — when this returns, the capture has landed
    /// (present or explicitly absent). The publish subscription is created
    /// BEFORE the commit: the capture pipeline runs synchronously whenever
    /// its adapter stubs are synchronous, and a hot Subject drops
    /// pre-subscriber emissions by design — the ticket wires its consumer
    /// before it pushes symbols, and the helper holds the same contract.
    /// (A late `FirstAsync()` subscribed only at the await and could miss
    /// the one-shot emission entirely, parking the fact forever.)
    /// </summary>
    private static async Task CommitAndAwaitCaptureAsync(OrderSizerRowViewModel row, Symbol symbol)
    {
        var published = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = row.ContextCaptures.Subscribe(_ => published.TrySetResult(RxVoid.Default));
        row.UpdateSymbol(symbol);
        await published.Task;
    }

    private static string FormatArgs(object?[] args) => string.Join(",", args ?? []);

    /// <summary>A custody table with both sides of BTC/USDT served.</summary>
    private static IReadOnlyDictionary<string, Money> BtcUsdtCustody(decimal baseAmount, decimal quoteAmount) =>
        new Dictionary<string, Money>
        {
            ["USDT"] = new(quoteAmount, "USDT"),
            ["BTC"] = new(baseAmount, "BTC"),
        };

    #endregion Fixtures

    #region Metadata Gate

    [Fact]
    public void UpdateSymbol_OnlyMetadataKnownPairs_ShowTheRow()
    {
        // Arrange
        using var row = CreateRow();

        // Act — a stock without a quote asset, a pair without metadata, an
        // unparsable-in-practice null, then a known pair.
        row.UpdateSymbol(Symbol.Parse("AAPL"));

        // Assert
        row.IsRowVisible.Should().BeFalse("stocks carry no round-size metadata");

        row.UpdateSymbol(Symbol.Parse("AAPL/USD"));
        row.IsRowVisible.Should().BeFalse("the pair is outside the metadata registry");

        row.UpdateSymbol(null);
        row.IsRowVisible.Should().BeFalse();
        row.IsSnapshotAvailable.Should().BeFalse();
        row.IsRiskModeAvailable.Should().BeFalse();

        row.UpdateSymbol(Symbol.Parse("BTC/USDT"));
        row.IsRowVisible.Should().BeTrue();
    }

    [Fact]
    public void UpdateSymbol_HidingTwice_ChurnsNothing()
    {
        // Arrange
        using var row = CreateRow();
        row.UpdateSymbol(Symbol.Parse("AAPL"));
        row.IsRowVisible.Should().BeFalse();

        // Act — a second hide lands on an already hidden row.
        row.UpdateSymbol(Symbol.Parse("AAPL/USD"));

        // Assert — the parked row stays parked with nothing to reset.
        row.IsRowVisible.Should().BeFalse();
        row.SnapshotDetail.Should().BeNull();
        row.BalanceSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task Commit_KnownPair_CapturesTheBalanceSnapshotAndDisplaysTheAmounts()
    {
        // Arrange
        using var row = CreateRow(balances: BtcUsdtCustody(0.8m, 50_000m));

        // Act
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));

        // Assert
        row.IsRowVisible.Should().BeTrue();
        row.IsSnapshotAvailable.Should().BeTrue();
        row.IsSnapshotMissing.Should().BeFalse();
        row.SnapshotDetail.Should().Contain("50000 USDT");
        row.SnapshotDetail.Should().Contain("0.8 BTC");
        row.SnapshotDetail.Should().Contain("NewOrder.SizerSnapshotDetail");
    }

    [Fact]
    public async Task CapturedSnapshots_NeverMergeInstruments()
    {
        // Arrange
        using var row = CreateRow(balances: new Dictionary<string, Money>
        {
            ["USDT"] = new(500m, "USDT"),
            ["BTC"] = new(0.4m, "BTC"),
            ["SOL"] = new(9m, "SOL"),
        });

        // Act
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.BalanceSnapshot!.Symbol.Normalized.Should().Be("BTC/USDT");
        row.BalanceSnapshot.BaseAvailable.Should().Be(0.4m);

        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("SOL/USDT"));

        // Assert — a snapshot never outlives its instrument.
        row.BalanceSnapshot!.Symbol.Normalized.Should().Be("SOL/USDT");
        row.BalanceSnapshot.BaseAvailable.Should().Be(9m);
        row.SnapshotDetail.Should().Contain("9 SOL");

        row.UpdateSymbol(Symbol.Parse("AAPL"));
        row.BalanceSnapshot.Should().BeNull("the hidden row owns no context from a previous instrument");
        row.IsSnapshotAvailable.Should().BeFalse();
        row.IsRiskModeAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CaptureFailure_IsAnExplicitAbsentSnapshot()
    {
        // Arrange — the trading face serves nothing.
        using var row = CreateRow();

        // Act
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));

        // Assert — absence is a state, never a fabricated zero.
        row.IsRowVisible.Should().BeTrue();
        row.IsSnapshotAvailable.Should().BeFalse();
        row.IsSnapshotMissing.Should().BeTrue();
        row.SnapshotDetail.Should().BeNull();
        row.BalanceSnapshot.Should().BeNull();

        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);

        var canExecute = await row.ApplyQuarterCommand.CanExecute.FirstAsync();
        canExecute.Should().BeFalse("there is no snapshot for the preset to ride");
    }

    #endregion Metadata Gate

    #region Balance Presets

    [Fact]
    public async Task BalanceSellPresets_ProposeFlooredFractionsOfTheCapturedBaseCustody()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        row.QuantityProposals.Subscribe(proposals.Add);
        StubBalances(BtcUsdtCustody(0.8m, 50_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);

        // Act
        await row.ApplyQuarterCommand.Execute(RxVoid.Default);
        await row.ApplyHalfCommand.Execute(RxVoid.Default);
        await row.ApplyWholeCommand.Execute(RxVoid.Default);

        // Assert
        proposals.Should().Equal("0.2", "0.4", "0.8");
        _ = _trading.Received(1).GetBalancesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BalanceBuyPreset_ConvertsQuoteCustodyAtTheEntryEstimate()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.5m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Buy, OrderType.Limit, 4.2m, null);
        row.QuantityProposals.Subscribe(proposals.Add);

        // Act
        await row.ApplyQuarterCommand.Execute(RxVoid.Default);

        // Assert — 1000 * 0.25 / 4.2 floors onto the BTC/USDT step 0.00001.
        proposals.Should().Equal("59.5238");
    }

    [Fact]
    public async Task BalanceBuyPreset_FallsBackToTheCapturedReferenceClose()
    {
        // Arrange — a market order carries no explicit price: the captured
        // reference close is the entry estimate.
        var proposals = new List<string>();
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.5m, 1_000m));
        StubReferenceClose(42m);
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Buy, OrderType.Market, null, null);
        row.ReferencePrice.Should().NotBeNull("the capture serves the entry estimate");
        row.QuantityProposals.Subscribe(proposals.Add);

        // Act
        await row.ApplyHalfCommand.Execute(RxVoid.Default);

        // Assert — 1000 * 0.5 / 42 floors onto the step.
        proposals.Should().Equal("11.90476");
    }

    [Fact]
    public async Task BalanceBuyPreset_WithoutAnEntryEstimate_CannotExecute()
    {
        // Arrange — neither a ticket price nor a captured reference.
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.5m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Buy, OrderType.Market, null, null);

        // Act
        var canExecute = await row.ApplyHalfCommand.CanExecute.FirstAsync();

        // Assert
        canExecute.Should().BeFalse();
    }

    [Fact]
    public async Task BalanceSellPreset_MissingBaseCustody_CannotExecute()
    {
        // Arrange — the source served the quote side only.
        using var row = CreateRow();
        StubBalances(new Dictionary<string, Money> { ["USDT"] = new(1_000m, "USDT") });
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.SnapshotDetail.Should().Contain("NewOrder.SizerAssetMissing");
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);

        // Act
        var canExecute = await row.ApplyQuarterCommand.CanExecute.FirstAsync();

        // Assert
        canExecute.Should().BeFalse("the custody the sell would spend was never served");
    }

    #endregion Balance Presets

    #region Risk Percent

    [Fact]
    public async Task RiskMode_VisibilityFollowsTheStopContext()
    {
        // Arrange
        using var row = CreateRow();
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.IsRiskModeAvailable.Should().BeFalse("no shape pushed yet");

        // Act & Assert — the ticket decides the semantics, the row reflects them.
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Market, null, null);
        row.IsRiskModeAvailable.Should().BeFalse("a market order lives without a stop anchor");

        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, 51_000m, null);
        row.IsRiskModeAvailable.Should().BeFalse("a plain limit carries no stop price");

        row.UpdateOrderShape(OrderSide.Sell, OrderType.StopLimit, 51_000m, 1_000m);
        row.IsRiskModeAvailable.Should().BeTrue("a stop-limit anchors |limit − trigger|");

        row.UpdateOrderShape(OrderSide.Sell, OrderType.TrailingStop, null, 100m);
        row.IsRiskModeAvailable.Should().BeTrue("a trailing stop anchors its trail distance");

        // A hidden row never shows the mode, however valid the distance.
        row.UpdateSymbol(Symbol.Parse("AAPL"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.TrailingStop, null, 100m);
        row.IsRiskModeAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RiskProposal_OnTheCapturedQuoteBalanceAcrossTheDistance()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        await row.InitializeDefaultsAsync();
        row.RiskPercentText.Should().Be("1");
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.StopLimit, 51_000m, 50m);
        row.QuantityProposals.Subscribe(proposals.Add);

        // Act — one percent of 1000 quote across a 50-unit stop distance.
        await row.ApplyRiskCommand.Execute(RxVoid.Default);

        // Assert
        proposals.Should().Equal("0.2");
    }

    [Fact]
    public async Task RiskProposal_AParserOwnedPercent_RidesTheSameMath()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.StopLimit, 51_000m, 50m);
        row.QuantityProposals.Subscribe(proposals.Add);

        // Act
        row.RiskPercentText = "0.5";
        await row.ApplyRiskCommand.Execute(RxVoid.Default);

        // Assert — half a percent halves the proposal.
        proposals.Should().Equal("0.1");
    }

    [Fact]
    public async Task RiskProposal_WithoutAStopDistance_CannotExecute()
    {
        // Arrange
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, 51_000m, null);

        // Act
        var canExecute = await row.ApplyRiskCommand.CanExecute.FirstAsync();

        // Assert
        canExecute.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeDefaults_LoadsTheStoredPercentOnlyOntoTheUntouchedField()
    {
        // Arrange — a stored preference of two and a half percent.
        using var row = CreateRow(storedPercent: 2.5);

        // Act
        await row.InitializeDefaultsAsync();

        // Assert
        row.RiskPercentText.Should().Be("2.5");

        row.RiskPercentText = "0.7";
        await row.InitializeDefaultsAsync();
        row.RiskPercentText.Should().Be("0.7", "a stored default never overwrites user intent");
    }

    [Fact]
    public async Task SettingsChanges_ReapplyTheDefaultWhileTheFieldStaysUntouched()
    {
        // Arrange
        using var row = CreateRow();
        await row.InitializeDefaultsAsync();
        row.RiskPercentText.Should().Be("1");

        // Act — a hot settings change of the sizing default.
        _settingsChanges.OnNext(new SettingsChange(
            SizerSettingsKeys.Section,
            SizerSettingsKeys.DefaultRiskPercent,
            1.0,
            3.0));

        // Assert
        row.RiskPercentText.Should().Be("3");

        // Act — the user owns the field afterwards.
        row.RiskPercentText = "0.7";
        _settingsChanges.OnNext(new SettingsChange(
            SizerSettingsKeys.Section,
            SizerSettingsKeys.DefaultRiskPercent,
            3.0,
            5.0));

        // Assert
        row.RiskPercentText.Should().Be("0.7", "the user's own text is never overwritten");

        // Act — unrelated settings churn passes through unapplied.
        _settingsChanges.OnNext(new SettingsChange("OtherSection", "OtherKey", null, 5.0));

        // Assert
        row.RiskPercentText.Should().Be("0.7");
    }

    #endregion Risk Percent

    #region Snapshot Freshness And Staleness

    [Fact]
    public async Task SizeClicks_NeverRefetch_EveryProposalRidesTheCapturedMoment()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);
        row.QuantityProposals.Subscribe(proposals.Add);

        // Act — the captured balance changes behind the row's back.
        StubBalances(BtcUsdtCustody(0.2m, 5_000m));
        await row.ApplyWholeCommand.Execute(RxVoid.Default);
        await row.ApplyHalfCommand.Execute(RxVoid.Default);

        // Assert — every click rides the captured moment; sizing itself
        // performs no fetch at all.
        proposals.Should().Equal("0.8", "0.4");
        _ = _trading.Received(1).GetBalancesAsync(Arg.Any<CancellationToken>());
        _ = _trading.Received(1).GetBarsAsync(
            Arg.Any<Symbol>(),
            Arg.Any<TimeFrame>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshSnapshot_ReCapturesTheBalanceForTheCommittedSymbol()
    {
        // Arrange
        var proposals = new List<string>();
        using var row = CreateRow();
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);
        row.QuantityProposals.Subscribe(proposals.Add);
        StubBalances(BtcUsdtCustody(0.2m, 5_000m));

        // Act — the deliberate freshness lever.
        await row.RefreshSnapshotCommand.Execute(RxVoid.Default);

        // Assert — the snapshot is current now; the proposal rides it.
        row.BalanceSnapshot!.BaseAvailable.Should().Be(0.2m);
        row.SnapshotDetail.Should().Contain("5000 USDT");
        await row.ApplyWholeCommand.Execute(RxVoid.Default);
        proposals.Should().Equal("0.2");
        _ = _trading.Received(2).GetBalancesAsync(Arg.Any<CancellationToken>());
        _ = _trading.Received(2).GetBarsAsync(
            Arg.Any<Symbol>(),
            Arg.Any<TimeFrame>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbsentSnapshot_IsRecoverableByTheDeliberateRefresh()
    {
        // Arrange — the first capture serves nothing.
        using var row = CreateRow();
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("BTC/USDT"));
        row.IsSnapshotAvailable.Should().BeFalse();

        // Act — the trading face comes back and the user refreshes.
        StubBalances(BtcUsdtCustody(0.8m, 1_000m));
        await row.RefreshSnapshotCommand.Execute(RxVoid.Default);

        // Assert
        row.IsSnapshotAvailable.Should().BeTrue();
        row.SnapshotDetail.Should().Contain("0.8 BTC");
    }

    [Fact]
    public async Task ZeroFloorProposal_ShowsTheStateLineInsteadOfProposingZero()
    {
        // Arrange — SOL/USDT trades 0.01-lots; the custody holds half a lot.
        var proposals = new List<string>();
        using var row = CreateRow();
        row.QuantityProposals.Subscribe(proposals.Add);
        StubBalances(new Dictionary<string, Money>
        {
            ["USDT"] = new(30m, "USDT"),
            ["SOL"] = new(0.005m, "SOL"),
        });
        await CommitAndAwaitCaptureAsync(row, Symbol.Parse("SOL/USDT"));
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);

        // Act
        await row.ApplyWholeCommand.Execute(RxVoid.Default);

        // Assert — zero is not a size: nothing is proposed, the row says so.
        proposals.Should().BeEmpty();
        row.HasProposalState.Should().BeTrue();
        row.ProposalState.Should().Contain("NewOrder.SizerNoProposal");

        // A shape push resets the stale note (a fresh proposal supersedes it).
        row.UpdateOrderShape(OrderSide.Sell, OrderType.Limit, null, null);
        row.HasProposalState.Should().BeFalse();
    }

    #endregion Snapshot Freshness And Staleness
}
