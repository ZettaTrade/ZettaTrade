namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.Tests.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Persisted workspace column geometry (T-038): the startup walk restores
/// stored, clamped or garbage-safe default widths before the first frame and
/// never rewrites what it just applied; a settled drag burst persists once at
/// the settled width; external writers apply live without echoing; and the
/// assistant column collapses with the panel's visibility while the user's
/// last width returns untouched on the next show. The detachable panels
/// (T-103) mirror the runtime's detached state and route the View menu's
/// toggle through it; a hidden assistant that still lives detached attaches
/// back first.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class WorkspaceLayoutViewModelTests
{
    static WorkspaceLayoutViewModelTests()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
    }

    /// <summary>
    /// How long a test waits for the debounced drag persistence to reach the
    /// settings service before calling the arrangement broken (never hit in
    /// a healthy pipeline — the burst write lands right after the VM's
    /// quarter-second quiet time; keeps a stalled test diagnosable).
    /// </summary>
    private static readonly TimeSpan SettleWaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a "nothing was written" assertion keeps watching past the
    /// drag quiet time before declaring the window closed — double the
    /// quiet time, so a late write would still land inside the observation.
    /// </summary>
    private static readonly TimeSpan EchoQuietBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>Width fuzz a float comparison tolerates (mirrors the VM's epsilon).</summary>
    private const double WidthEpsilon = 0.01;

    /// <summary>Width of the visible splitter track column (the grid's fixed gutter).</summary>
    private const double SplitterTrackWidth = 8;

    #region Restore

    /// <summary>
    /// The walk with the third-party default reads keeps the layout every
    /// session had before the splitters existed.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoStoredWidths_KeepsTheShippedDefaults()
    {
        var kit = await CreateKitAsync();

        CarriesPixelWidth(kit.Layout.BookColumnWidth, UiSettingsKeys.DefaultBookPanelWidth);
        CarriesPixelWidth(kit.Layout.AiColumnWidth, UiSettingsKeys.DefaultAiPanelWidth);
        kit.Layout.AiColumnMinWidth.Should().Be(UiSettingsKeys.MinAiPanelWidth);
        CarriesPixelWidth(kit.Layout.AiSplitterColumnWidth, SplitterTrackWidth);
    }

    /// <summary>
    /// The stored widths apply at the walk, and the walk never writes the
    /// values it just read back.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresTheStoredWidths_WithoutRewritingThem()
    {
        var kit = await CreateKitAsync(storedBook: 420, storedAi: 260);

        CarriesPixelWidth(kit.Layout.BookColumnWidth, 420);
        CarriesPixelWidth(kit.Layout.AiColumnWidth, 260);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// Whatever the settings reader let through non-finite (an out-of-range
    /// JSON magnitude such as <c>1e999</c>, a hand-edited file) falls back to
    /// the shipped defaults instead of poisoning the grid.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_GarbageStoredWidths_FallBackToTheDefaults()
    {
        var kit = await CreateKitAsync(storedBook: double.PositiveInfinity, storedAi: double.NaN);

        CarriesPixelWidth(kit.Layout.BookColumnWidth, UiSettingsKeys.DefaultBookPanelWidth);
        CarriesPixelWidth(kit.Layout.AiColumnWidth, UiSettingsKeys.DefaultAiPanelWidth);
    }

    /// <summary>
    /// A stored width below the usable minimum clamps to that minimum on
    /// restore, silently (the clamp is not a user choice worth a rewrite).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_BelowMinimumStoredWidths_ClampToTheUsableMinimum()
    {
        var kit = await CreateKitAsync(storedBook: 100, storedAi: -50);

        CarriesPixelWidth(kit.Layout.BookColumnWidth, UiSettingsKeys.MinBookPanelWidth);
        CarriesPixelWidth(kit.Layout.AiColumnWidth, UiSettingsKeys.MinAiPanelWidth);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    #endregion

    #region Detached Panels (T-103)

    /// <summary>
    /// The mirror starts from the runtime's CURRENT state: a session whose
    /// restore already detached a panel (the coordinator was built before
    /// the layout ViewModel resolves in the walk) shows it detached from
    /// the first frame.
    /// </summary>
    [Fact]
    public async Task TheDetachMirror_StartsFromTheRuntimeState()
    {
        var detacher = new FakeDetachedPanelCoordinator();
        await detacher.DetachPanelWindowAsync(PanelDetachId.Book, CancellationToken.None);
        var kit = await CreateKitAsync(detacher: detacher);

        kit.Layout.IsBookDetached.Should().BeTrue();
        kit.Layout.IsChartDetached.Should().BeFalse();
        kit.Layout.IsAssistantDetached.Should().BeFalse();
    }

    /// <summary>
    /// The View menu's toggle routes the panel id to the runtime: one
    /// execution detaches, the same execution again attaches — the
    /// checkbox's flip IS the panel's window, nothing more.
    /// </summary>
    [Fact]
    public async Task TheToggleCommand_RoutesThePanelIdToTheRuntime()
    {
        var kit = await CreateKitAsync();

        await kit.Layout.ToggleDetachCommand.Execute(PanelDetachId.Chart);
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Detacher.Toggles.Should().ContainInConsecutiveOrder(PanelDetachId.Chart);
        kit.Detacher.IsDetached(PanelDetachId.Chart).Should().BeTrue();
        kit.Layout.IsChartDetached.Should().BeTrue();

        await kit.Layout.ToggleDetachCommand.Execute(PanelDetachId.Chart);
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Detacher.Toggles.Should().ContainInConsecutiveOrder(PanelDetachId.Chart, PanelDetachId.Chart);
        kit.Detacher.IsDetached(PanelDetachId.Chart).Should().BeFalse();
        kit.Layout.IsChartDetached.Should().BeFalse();
    }

    /// <summary>
    /// The runtime's own emits (every detach leg including the restore and
    /// the window's own close) drive the mirror: the menu checkbox stays
    /// truthful no matter WHICH route moved the panel.
    /// </summary>
    [Fact]
    public async Task TheRuntimeEmits_DriveTheMirror()
    {
        var kit = await CreateKitAsync();

        await kit.Detacher.DetachPanelWindowAsync(PanelDetachId.Assistant, CancellationToken.None);
        await AvaloniaDispatcherPump.DrainAsync();
        kit.Layout.IsAssistantDetached.Should().BeTrue();

        await kit.Detacher.AttachPanelWindowAsync(PanelDetachId.Assistant, CancellationToken.None);
        await AvaloniaDispatcherPump.DrainAsync();
        kit.Layout.IsAssistantDetached.Should().BeFalse();
    }

    /// <summary>
    /// A hidden assistant that still lives in its own window attaches back
    /// FIRST: the collapse keeps one truth — the panel is inline whenever
    /// its column collapses (the inline note and the collapsed column never
    /// disagree).
    /// </summary>
    [Fact]
    public async Task AHiddenAssistant_ThatLivesDetached_AttachesBackInline()
    {
        var kit = await CreateKitAsync();
        await kit.Detacher.DetachPanelWindowAsync(PanelDetachId.Assistant, CancellationToken.None);

        await kit.Chat.ToggleAsync();
        await WaitUntilAsync(() => !kit.Detacher.IsDetached(PanelDetachId.Assistant));
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Layout.IsAssistantDetached.Should().BeFalse();
        CarriesPixelWidth(kit.Layout.AiColumnWidth, 0);
    }

    /// <summary>Give an async leg the settle budget to land, then fail loud if it never did.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SettleWaitBudget;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue("the async leg never landed within the settle budget");
    }

    #endregion

    #region Settled Drag Persistence

    /// <summary>
    /// A settled book drag persists ONCE, at the settled width: the burst's
    /// per-pixel arrangement collapses behind the debounce (a preview write
    /// of an intermediate width never reaches the settings file).
    /// </summary>
    [Fact]
    public async Task DragSettles_TheBookBurstPersistsOnceAtTheSettledWidth()
    {
        var kit = await CreateKitAsync();

        // Act: one drag burst — a few writebacks one pixel apart, then settle
        kit.Layout.BookColumnWidth = new GridLength(420);
        kit.Layout.BookColumnWidth = new GridLength(431);
        var signal = ArmWriteSignal(kit.Settings);
        kit.Layout.BookColumnWidth = new GridLength(440);

        // Assert
        var settled = await signal.Task.WaitAsync(SettleWaitBudget);
        settled.Should().Be(440, "the burst persists the width the drag settled at");
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.BookPanelWidth,
            440d);

        // Act: a second, later burst is a new difference and persists too
        kit.Layout.BookColumnWidth = new GridLength(460);
        settled = await ArmWriteSignal(kit.Settings).Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(460);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.BookPanelWidth,
            460d);
    }

    /// <summary>
    /// The assistant drag persists the same way, through its own pipeline and
    /// its own key.
    /// </summary>
    [Fact]
    public async Task DragSettles_TheAssistantBurstPersistsOnceAtTheSettledWidth()
    {
        var kit = await CreateKitAsync();

        kit.Layout.AiColumnWidth = new GridLength(340);
        kit.Layout.AiColumnWidth = new GridLength(352);
        var signal = ArmWriteSignal(kit.Settings);
        kit.Layout.AiColumnWidth = new GridLength(360);

        var settled = await signal.Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(360);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            360d);
    }

    #endregion

    #region External Writers

    /// <summary>
    /// An external writer re-applies the book width live without a restart:
    /// matching keys apply, unrelated keys and non-double payloads drop.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_AppliesBookWidthLive_WithoutEchoingIt()
    {
        var kit = await CreateKitAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.Theme,
            "dark",
            "light"));
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.BookPanelWidth,
            UiSettingsKeys.DefaultBookPanelWidth,
            "not a number"));
        CarriesPixelWidth(kit.Layout.BookColumnWidth, UiSettingsKeys.DefaultBookPanelWidth,
            "an unrelated key and a non-double payload both leave the width alone");

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.BookPanelWidth,
            UiSettingsKeys.DefaultBookPanelWidth,
            340.0));

        CarriesPixelWidth(kit.Layout.BookColumnWidth, 340);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// The assistant width applies live the same way — and also updates the
    /// capture a later show would restore.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_AppliesAssistantWidthLive_AndFeedsTheCapture()
    {
        var kit = await CreateKitAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.BookPanelWidth,
            300.0,
            340.0));
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            UiSettingsKeys.DefaultAiPanelWidth,
            "520"));
        CarriesPixelWidth(kit.Layout.AiColumnWidth, UiSettingsKeys.DefaultAiPanelWidth,
            "a non-double payload leaves the assistant width alone");

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            UiSettingsKeys.DefaultAiPanelWidth,
            280.0));

        CarriesPixelWidth(kit.Layout.AiColumnWidth, 280);

        // The external truth is what a show would restore
        await kit.Chat.ToggleAsync();
        await kit.Chat.ToggleAsync();
        CarriesPixelWidth(kit.Layout.AiColumnWidth, 280, "the captured external width returns on the next show");
    }

    /// <summary>
    /// The applied external change is silent on the settings service, and a
    /// real drag AFTER the application still persists its (now different)
    /// settled width.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_TheEchoIsSilent_ARealAssistantDragStillPersists()
    {
        var kit = await CreateKitAsync();

        // Act: an external writer applies a width live
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            UiSettingsKeys.DefaultAiPanelWidth,
            280.0));

        // Assert: the application stayed silent
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            280.0);

        // Act: the user re-dragged the column after the application
        var signal = ArmWriteSignal(kit.Settings);
        kit.Layout.AiColumnWidth = new GridLength(360);

        var settled = await signal.Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(360);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            360d);
    }

    #endregion

    #region Assistant Visibility

    /// <summary>
    /// Hiding the assistant collapses its column, its splitter track and its
    /// min clamp to zero — without a width write (the panel choice is the
    /// chat's persisted state, not the layout's).
    /// </summary>
    [Fact]
    public async Task HidingTheAssistant_CollapsesItsGeometry_WithoutRewritingTheWidth()
    {
        var kit = await CreateKitAsync();

        await kit.Chat.ToggleAsync();

        CarriesPixelWidth(kit.Layout.AiColumnWidth, 0);
        kit.Layout.AiColumnMinWidth.Should().Be(0, "the collapsed column must be allowed to reach zero");
        CarriesPixelWidth(kit.Layout.AiSplitterColumnWidth, 0, "no dead grab handle in an empty gutter");
        CarriesPixelWidth(kit.Layout.BookColumnWidth, UiSettingsKeys.DefaultBookPanelWidth,
            "the book side has no hidden state");
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// Showing the panel restores the width the user saw (a settled drag
    /// before a hide is honored), the min clamp and the splitter track come
    /// back — and the show itself stays silent.
    /// </summary>
    [Fact]
    public async Task HideThenShow_RestoresTheCapturedWidth_AndTheShowStaysSilent()
    {
        var kit = await CreateKitAsync();

        // Act: the assistant was dragged to 380 and the drag settled
        var signal = ArmWriteSignal(kit.Settings);
        kit.Layout.AiColumnWidth = new GridLength(380);
        await signal.Task.WaitAsync(SettleWaitBudget);

        await kit.Chat.ToggleAsync();
        CarriesPixelWidth(kit.Layout.AiColumnWidth, 0);

        await kit.Chat.ToggleAsync();

        CarriesPixelWidth(kit.Layout.AiColumnWidth, 380, "the captured width returns untouched");
        kit.Layout.AiColumnMinWidth.Should().Be(UiSettingsKeys.MinAiPanelWidth);
        CarriesPixelWidth(kit.Layout.AiSplitterColumnWidth, SplitterTrackWidth);

        await WaitPastQuietTimeAsync();
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.AiPanelWidth,
            380d);
    }

    /// <summary>
    /// A walk that restores a HIDDEN panel keeps the collapse and banks the
    /// stored assistant width as the show capture (nothing the walk reads is
    /// lost by the hidden startup).
    /// </summary>
    [Fact]
    public async Task AHiddenWalk_KeepsTheCollapse_AndBanksTheStoredWidthAsTheShowCapture()
    {
        var kit = await CreateKitAsync(storedBook: 420, storedAi: 260, persistedVisibility: false);

        CarriesPixelWidth(kit.Layout.BookColumnWidth, 420, "the book side restores independently of the panel");
        CarriesPixelWidth(kit.Layout.AiColumnWidth, 0, "the persisted-hidden panel keeps its column collapsed");
        kit.Layout.AiColumnMinWidth.Should().Be(0);
        CarriesPixelWidth(kit.Layout.AiSplitterColumnWidth, 0);

        await kit.Chat.ToggleAsync();

        CarriesPixelWidth(kit.Layout.AiColumnWidth, 260, "the stored width is the capture the show restores");
        kit.Layout.AiColumnMinWidth.Should().Be(UiSettingsKeys.MinAiPanelWidth);
        CarriesPixelWidth(kit.Layout.AiSplitterColumnWidth, SplitterTrackWidth);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    #endregion

    #region Factories

    /// <summary>
    /// A wired kit: the settings substitute answers the book/AI width reads
    /// per key (and the chat's visibility read), the changes stream is an
    /// open subject, and the chat+layout pair stands where the startup walk
    /// leaves it — both constructed (the layout ctor rides the chat's
    /// visibility), both initialized in the walk's registration order.
    /// </summary>
    private static async Task<LayoutKit> CreateKitAsync(
        double storedBook = UiSettingsKeys.DefaultBookPanelWidth,
        double storedAi = UiSettingsKeys.DefaultAiPanelWidth,
        bool persistedVisibility = UiSettingsKeys.DefaultShowAiPanel,
        FakeDetachedPanelCoordinator? detacher = null)
    {
        detacher ??= new FakeDetachedPanelCoordinator();
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedVisibility);
        settings.GetAsync<double>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == UiSettingsKeys.BookPanelWidth),
                Arg.Any<double>())
            .Returns(storedBook);
        settings.GetAsync<double>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == UiSettingsKeys.AiPanelWidth),
                Arg.Any<double>())
            .Returns(storedAi);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var chat = new ChatViewModel(
            settings,
            ChatTests.Completion(),
            ChatTests.IdleContext(),
            new WorkspacePromptComposer(),
            ChatTests.Localization(),
            NullLogger<ChatViewModel>.Instance);

        // The walk order: the layout resolves right after the chat, then both
        // initialize in the registration order — the chat's visibility restore
        // lands before the width restore reads the settings.
        var layout = new WorkspaceLayoutViewModel(
            settings,
            chat,
            detacher,
            NullLogger<WorkspaceLayoutViewModel>.Instance);
        await chat.InitializeAsync();
        await layout.InitializeAsync();

        return new LayoutKit(layout, chat, settings, changes, detacher);
    }

    /// <summary>
    /// Arm the write signal: the NEXT settings write with a double payload
    /// completes the returned task with its value (a deterministic wait for
    /// the debounced burst; a second burst arms a fresh signal, since each
    /// arm re-configures the write handler).
    /// </summary>
    private static TaskCompletionSource<double> ArmWriteSignal(ISettingsService settings)
    {
        var signal = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.SetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(callInfo =>
            {
                signal.TrySetResult(callInfo.ArgAt<double>(2));
                return Task.CompletedTask;
            });
        return signal;
    }

    /// <summary>Give the quiet-time window a chance to invalidate a silence claim.</summary>
    private static Task WaitPastQuietTimeAsync() => Task.Delay(EchoQuietBudget);

    /// <summary>Assert an absolute (pixel) grid length carries the expected width.</summary>
    private static void CarriesPixelWidth(GridLength actual, double expected, string because = "")
    {
        actual.IsAbsolute.Should().BeTrue("the workspace widths are absolute pixel lengths");
        actual.Value.Should().BeApproximately(expected, WidthEpsilon, because);
    }

    #endregion
}

/// <summary>One wired layout with every collaborator a test drives.</summary>
internal sealed record LayoutKit(
    WorkspaceLayoutViewModel Layout,
    ChatViewModel Chat,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    FakeDetachedPanelCoordinator Detacher);
