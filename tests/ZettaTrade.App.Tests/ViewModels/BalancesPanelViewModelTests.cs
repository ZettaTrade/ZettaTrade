namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Balances panel state machine: the panel starts unloaded, one load shows
/// a brief loading state and lands the live rows; each source displays
/// exactly what its adapter serves — provided rows, the explicit
/// not-provided state, or the localized fault of a failed query — never a
/// zero figure; the aggregate sums per asset; a refresh follows the live
/// balances; a whole-snapshot fault surfaces as the localized error line;
/// and the paper reset flow (T-192) hands the dialog's parsed choice to the
/// reset service, reports the finished summary, reloads the figures, and
/// stays silent on a declined dialog or a refused confirmation while a
/// fault rides the localized failure line.
/// </summary>
public sealed class BalancesPanelViewModelTests
{
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IPaperResetService _paperReset = Substitute.For<IPaperResetService>();

    public BalancesPanelViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "[Trading.BalancesFetchFailedFormat:socket boom]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    [Fact]
    public void InitialState_IsUnloadedWithNoFigures()
    {
        // Arrange
        var binance = CreateTrader("binance", "Binance", Balances(("USDT", 4000m)));
        var viewModel = CreateViewModel([binance], CreatePaperTrader(("USDT", 1000m)));

        // Assert: nothing is rendered before the first snapshot lands.
        viewModel.HasLoaded.Should().BeFalse();
        viewModel.IsLoading.Should().BeFalse();
        viewModel.Sources.Should().BeEmpty();
        viewModel.Aggregated.Should().BeEmpty();
        viewModel.HasAggregated.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WithProvidedSources_ShowsLiveRowsAndTheAggregate()
    {
        // Arrange: two serving adapters plus one market-data-only adapter.
        var binance = CreateTrader("binance", "Binance", Balances(("USDT", 4000m), ("BTC", 0.4m)));
        var demo = CreateMarketDataOnly("demo", "Demo");
        var paper = CreatePaperTrader(("USDT", 1000m));
        var viewModel = CreateViewModel([binance, demo], paper);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the per-exchange sections show exactly the served state.
        viewModel.HasLoaded.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse("the load has completed");
        viewModel.ErrorMessage.Should().BeNull();

        viewModel.Sources.Should().HaveCount(3);
        viewModel.Sources[0].AdapterName.Should().Be("Binance");
        viewModel.Sources[0].IsProvided.Should().BeTrue();
        viewModel.Sources[0].Rows.Select(row => row.Asset).Should().Equal(["BTC", "USDT"]);
        viewModel.Sources[0].Rows.Single(row => row.Asset == "USDT").Amount.Should().Be("4000");
        viewModel.Sources[0].Rows.Single(row => row.Asset == "BTC").Amount.Should().Be("0.4");
        viewModel.Sources[1].AdapterName.Should().Be("Demo");
        viewModel.Sources[1].IsNotProvided.Should().BeTrue("the market-data-only adapter cannot serve balances");
        viewModel.Sources[1].Rows.Should().BeEmpty();
        viewModel.Sources[2].AdapterName.Should().Be("Paper Trading");
        viewModel.Sources[2].IsProvided.Should().BeTrue();

        // The aggregate sums the providing sources, not the not-provided one.
        viewModel.Aggregated.Select(row => row.Asset).Should().Equal(["BTC", "USDT"]);
        viewModel.Aggregated.Single(row => row.Asset == "USDT").Amount.Should().Be("5000", "4000 (Binance) + 1000 (paper)");
        viewModel.Aggregated.Single(row => row.Asset == "BTC").Amount.Should().Be("0.4");
        viewModel.HasAggregated.Should().BeTrue();
    }

    [Fact]
    public async Task LoadCommand_WithProvidedButEmptyBalances_ShowsTheExplicitEmptyState()
    {
        // Arrange: the adapter honestly serves an empty balance set.
        var paper = CreatePaperTrader();
        var viewModel = CreateViewModel([], paper);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: empty served is an empty display, never a zero row.
        viewModel.Sources.Should().HaveCount(1);
        viewModel.Sources[0].IsProvided.Should().BeTrue();
        viewModel.Sources[0].HasRows.Should().BeFalse();
        viewModel.Sources[0].IsProvidedButEmpty.Should().BeTrue();
        viewModel.Aggregated.Should().BeEmpty();
        viewModel.HasAggregated.Should().BeFalse();
    }

    [Fact]
    public async Task LoadCommand_WithFaultingSource_ShowsTheLocalizedFaultInsteadOfZeros()
    {
        // Arrange: one adapter's query faults; the others must still serve.
        var broken = CreateTrader("broken", "Broken Trader", Balances(("USDT", 500m)));
        broken.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, Money>>>(callInfo =>
                throw new InvalidOperationException("socket boom"));
        var paper = CreatePaperTrader(("USDT", 1000m));
        var viewModel = CreateViewModel([broken], paper);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().BeNull("the load itself never failed: only the source did");
        viewModel.Sources[0].IsFailed.Should().BeTrue();
        viewModel.Sources[0].IsProvided.Should().BeFalse();
        viewModel.Sources[0].FailureMessage.Should().Be("[Trading.BalancesFetchFailedFormat:socket boom]");
        viewModel.Sources[0].Rows.Should().BeEmpty();
        viewModel.Sources[1].IsFailed.Should().BeFalse();

        // The aggregate keeps the honest source's sum and nothing from the failed one.
        viewModel.Aggregated.Single(row => row.Asset == "USDT").Amount.Should().Be("1000");
    }

    [Fact]
    public async Task LoadCommand_WhenTheSnapshotAborts_ShowsTheLocalizedErrorLine()
    {
        // Arrange: the adapter cancels its query — the snapshot aborts
        // entirely, so the panel reports the whole-load failure line.
        var cancelled = new CancellationToken(canceled: true);
        var paper = CreatePaperTrader(("USDT", 1000m));
        paper.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, Money>>>(callInfo =>
                Task.FromCanceled<IReadOnlyDictionary<string, Money>>(cancelled));
        var viewModel = CreateViewModel([], paper);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.ErrorMessage.Should().StartWith("[Trading.BalancesLoadFailedFormat:");
        viewModel.ErrorMessage.Should().EndWith("]", "the whole-snapshot fault detail rides the localized line");
    }

    [Fact]
    public async Task LoadCommand_RefreshedAfterABalanceChange_FollowsTheLiveBalances()
    {
        // Arrange: the adapter serves one balance set, then another (the
        // paper account reflects the fills between the two loads).
        var paper = CreatePaperTrader(("USDT", 100_000m));
        var viewModel = CreateViewModel([], paper);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        paper.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(Balances(("USDT", 99_900m), ("BTC", 0.01m)));
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the section shows the live figures, not the stale ones.
        viewModel.Sources[0].Rows.Select(row => row.Asset).Should().Equal(["BTC", "USDT"]);
        viewModel.Sources[0].Rows.Single(row => row.Asset == "USDT").Amount.Should().Be("99900");
        viewModel.Aggregated.Single(row => row.Asset == "USDT").Amount.Should().Be("99900");
        viewModel.Aggregated.Single(row => row.Asset == "BTC").Amount.Should().Be("0.01");
    }

    [Fact]
    public async Task ResetPaperCommand_WithAnAppliedChoice_RunsTheResetReloadsAndReportsTheOutcome()
    {
        // Arrange: the dialog hands the parsed starting balance back; the
        // service reports the finished summary. The post-reset reload reads
        // the wiped-and-reseeded surface.
        var paper = CreatePaperTrader(("USDT", 25_000m));
        _paperReset.GetStartingBalanceAsync(Arg.Any<CancellationToken>()).Returns(100_000m);
        _paperReset.ResetAsync(25_000m, Arg.Any<CancellationToken>())
            .Returns(PaperResetOutcome.Finished("[Trading.PaperResetDoneFormat:25000|0]"));
        var viewModel = CreateViewModel([], paper);
        using var dialog = viewModel.ResetRequested.RegisterHandler(context =>
            context.SetOutput(25_000m));

        // Act
        await viewModel.ResetPaperCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the service ran with the dialog's choice, the outcome line
        // carries the summary, and the figures reloaded through the shared
        // load pipeline.
        await _paperReset.Received(1).ResetAsync(25_000m, Arg.Any<CancellationToken>());
        viewModel.OutcomeMessage.Should().Be("[Trading.PaperResetDoneFormat:25000|0]");
        viewModel.HasOutcomeMessage.Should().BeTrue();
        viewModel.HasErrorMessage.Should().BeFalse();
        viewModel.Sources.Should().ContainSingle("the flow reloaded the figures");
        viewModel.Sources[0].Rows.Single(row => row.Asset == "USDT").Amount.Should().Be("25000");
    }

    [Fact]
    public async Task ResetPaperCommand_WhenTheDialogClosesWithoutAChoice_NothingTouchedAndSilent()
    {
        // Arrange: the dialog closed without a parsed choice (a cancel).
        var paper = CreatePaperTrader(("USDT", 100_000m));
        _paperReset.GetStartingBalanceAsync(Arg.Any<CancellationToken>()).Returns(100_000m);
        var viewModel = CreateViewModel([], paper);
        using var dialog = viewModel.ResetRequested.RegisterHandler(context =>
            context.SetOutput(null));

        // Act
        await viewModel.ResetPaperCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the account stays exactly as it was, nothing is reported,
        // the panel is not reloaded.
        await _paperReset.DidNotReceive().ResetAsync(Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        viewModel.OutcomeMessage.Should().BeNull();
        viewModel.HasErrorMessage.Should().BeFalse();
        viewModel.Sources.Should().BeEmpty("a closed dialog never reloads the panel");
    }

    [Fact]
    public async Task ResetPaperCommand_WhenTheConfirmationIsRefused_SilentUntouched()
    {
        // Arrange: the dialog handed a choice, the service reports the
        // declined outcome — the confirmation refused, so nothing changed.
        var paper = CreatePaperTrader(("USDT", 100_000m));
        _paperReset.GetStartingBalanceAsync(Arg.Any<CancellationToken>()).Returns(100_000m);
        _paperReset.ResetAsync(25_000m, Arg.Any<CancellationToken>())
            .Returns(PaperResetOutcome.Declined());
        var viewModel = CreateViewModel([], paper);
        using var dialog = viewModel.ResetRequested.RegisterHandler(context =>
            context.SetOutput(25_000m));

        // Act
        await viewModel.ResetPaperCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.OutcomeMessage.Should().BeNull("a refused confirmation stays silent");
        viewModel.HasErrorMessage.Should().BeFalse();
        viewModel.Sources.Should().BeEmpty("a refused confirmation never reloads the panel");
    }

    [Fact]
    public async Task ResetPaperCommand_WhenTheResetFaults_ShowsTheLocalizedFailureLine()
    {
        // Arrange: the service faults mid-flow (a network/adaptor error).
        var paper = CreatePaperTrader(("USDT", 100_000m));
        _paperReset.GetStartingBalanceAsync(Arg.Any<CancellationToken>()).Returns(100_000m);
        _paperReset.ResetAsync(25_000m, Arg.Any<CancellationToken>())
            .Returns<Task<PaperResetOutcome>>(callInfo =>
                throw new InvalidOperationException("socket boom"));
        var viewModel = CreateViewModel([], paper);
        using var dialog = viewModel.ResetRequested.RegisterHandler(context =>
            context.SetOutput(25_000m));

        // Act
        await viewModel.ResetPaperCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the fault rides the existing error lane, no outcome line.
        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.ErrorMessage.Should().StartWith("[Trading.PaperResetFailedFormat:");
        viewModel.ErrorMessage.Should().EndWith("]", "the fault detail rides the localized line");
        viewModel.OutcomeMessage.Should().BeNull();
    }

    private static BalancesAggregationService CreateService(
        IReadOnlyList<IMarketDataAdapter> registered,
        ITradingAdapter trading) =>
        new(
            registered,
            trading,
            Substitute.For<ILogger<BalancesAggregationService>>());

    private BalancesPanelViewModel CreateViewModel(
        IReadOnlyList<IMarketDataAdapter> registered,
        ITradingAdapter trading) =>
        new(
            CreateService(registered, trading),
            _paperReset,
            _localization,
            Substitute.For<ILogger<BalancesPanelViewModel>>());

    private static ITradingAdapter CreateTrader(
        string id,
        string name,
        IReadOnlyDictionary<string, Money> balances)
    {
        var trader = Substitute.For<ITradingAdapter>();
        trader.Id.Returns(id);
        trader.Name.Returns(name);
        trader.GetBalancesAsync(Arg.Any<CancellationToken>()).Returns(balances);
        return trader;
    }

    private static ITradingAdapter CreatePaperTrader(params (string Asset, decimal Amount)[] balances) =>
        CreateTrader("paper", "Paper Trading", Balances(balances));

    private static IMarketDataAdapter CreateMarketDataOnly(string id, string name)
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns(id);
        adapter.Name.Returns(name);
        return adapter;
    }

    private static IReadOnlyDictionary<string, Money> Balances(params (string Asset, decimal Amount)[] entries) =>
        entries.ToDictionary(entry => entry.Asset, entry => new Money(entry.Amount, entry.Asset));

    private static string FormatArgs(object[] args) =>
        string.Join(",", args.Select(argument => argument ?? "null"));
}
