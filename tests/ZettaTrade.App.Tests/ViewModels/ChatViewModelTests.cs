namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using ReactiveUI;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// Chat panel state: the visibility is persisted state (the startup walk
/// restores <c>UI.ShowAiPanel</c> before any window opens, every user toggle
/// persists immediately, the restore and the changes-stream echo never
/// rewrite the value they just applied, and external writers apply live);
/// the send pipeline turns the input into a workspace-context completion
/// where the user turn lands first, the answer appends as an assistant turn,
/// an unavailable assistant reports an honest in-chat error line, and no
/// request ever races another. The Stop command (T-158) halts an in-flight
/// reply through the engine's existing cancellation seam: it offers itself
/// only while a reply runs, a raced click after the settle is a no-op, and
/// a stopped request leaves the accepted turn unanswered with no error line.
/// </summary>
public sealed class ChatViewModelTests
{
    /// <summary>
    /// How long a test waits for the send pipeline to reach its next
    /// observable signal before calling the arrangement broken (never hit in
    /// a healthy pipeline; keeps a stalled test diagnosable).
    /// </summary>
    private static readonly TimeSpan SendWaitBudget = TimeSpan.FromSeconds(10);

    #region Visibility

    /// <summary>
    /// No persisted choice (the third-party default read) keeps the panel the
    /// layout every session had before the toggle existed.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoPersistedChoice_KeepsThePanelVisible()
    {
        var chat = CreateChat(out _, UiSettingsKeys.DefaultShowAiPanel);

        await chat.InitializeAsync();

        chat.IsAiChatVisible.Should().BeTrue();
    }

    /// <summary>
    /// The persisted hidden panel applies at the walk, and the walk never
    /// writes the value it just read back.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedHiddenPanel_WithoutRewritingIt()
    {
        var chat = CreateChat(out var settings, false);

        await chat.InitializeAsync();

        chat.IsAiChatVisible.Should().BeFalse();
        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// A user toggle on the default layout hides the panel and persists the
    /// flip; the toggle reports the visibility AFTER the flip so the menu
    /// asks the View to focus the input exactly when the panel shows.
    /// </summary>
    [Fact]
    public async Task Toggle_HidesTheVisiblePanel_PersistsAndReportsTheFlip()
    {
        var chat = CreateChat(out var settings, UiSettingsKeys.DefaultShowAiPanel);
        await chat.InitializeAsync();

        var visible = await chat.ToggleAsync();

        visible.Should().BeFalse("the default layout is visible, so the first toggle hides");
        chat.IsAiChatVisible.Should().BeFalse();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);
    }

    /// <summary>
    /// A toggle on the persisted-hidden panel shows it again and persists the
    /// flip the same way; the second toggle back is a new difference and is
    /// persisted too (both states round-trip).
    /// </summary>
    [Fact]
    public async Task Toggle_AHiddenPanel_ShowsItAgainAndPersists()
    {
        var chat = CreateChat(out var settings, false);
        await chat.InitializeAsync();

        var visible = await chat.ToggleAsync();

        visible.Should().BeTrue();
        chat.IsAiChatVisible.Should().BeTrue();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true);

        await chat.ToggleAsync();

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);
    }

    /// <summary>
    /// An external writer re-applies live without a restart: matching keys
    /// apply, unrelated keys and non-bool values are dropped. The applied
    /// value never echoes back through the persistence pipeline — the cache
    /// led the property change, so the applied change saves nothing.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_AppliesVisibilityLive_WithoutEchoingIt()
    {
        var (chat, changes, _) = CreateChatWithChanges(UiSettingsKeys.DefaultShowAiPanel);

        await chat.InitializeAsync();

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame,
            "H1",
            "H4"));
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true,
            "true"));

        chat.IsAiChatVisible.Should().BeTrue("an unrelated key and a non-bool value both leave the panel alone");
    }

    /// <summary>
    /// The applied external change is silent on the settings service (no
    /// echo rewrite) and lands on the property; a user toggle AFTER the
    /// external application still persists its (now different) state.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_EchoIsSilent_TogglesStillPersist()
    {
        var (chat, changes, settings) = CreateChatWithChanges(UiSettingsKeys.DefaultShowAiPanel);

        // Arrange: the default layout is visible, the walk restored it
        await chat.InitializeAsync();

        // Act: an external writer (hot-reload, another session) hides the panel
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true,
            false));

        // Assert: the flip applied live and the applied value stayed silent
        chat.IsAiChatVisible.Should().BeFalse("the external change applies without a restart");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);

        // Act: the user shows the panel again — a real difference against the
        // externally applied state, so it persists this time
        await chat.ToggleAsync();

        chat.IsAiChatVisible.Should().BeTrue();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true);
    }

    /// <summary>
    /// BUG-053: two rapid toggles, the Second landing while the first write
    /// is still in flight. The pre-fix completion-driven cache kept the OLD
    /// value until the first write landed, so the second toggle (back to the
    /// cached value) was filtered out as "already saved" — the settings file
    /// then kept the first toggle's value and the restart restored the wrong
    /// visibility. The cache LEADS now: the second toggle is a real
    /// difference, both writes are queued (Concat serializes them strictly
    /// in issuance order), and the last value wins in the file.
    /// </summary>
    [Fact]
    public async Task TwoRapidToggles_WithTheFirstWriteInFlight_PersistBothInIssuanceOrder()
    {
        var kit = ChatTests.CreateKit(persistedVisibility: true);
        await kit.Chat.InitializeAsync();

        // The first write parks: the second toggle lands while it is in
        // flight — exactly the window the stale-cache race corrupted.
        var firstWriteReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokedWrites = new List<bool>();
        var secondWriteInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeSequence = 0;
        kit.Settings
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(call =>
            {
                invokedWrites.Add(call.ArgAt<bool>(2));
                if (Interlocked.Increment(ref writeSequence) == 2)
                {
                    secondWriteInvoked.TrySetResult();
                }

                return invokedWrites.Count == 1 ? firstWriteReleased.Task : Task.CompletedTask;
            });

        // Act: the rapid toggle pair. The first write parks; the second
        // toggle is a real difference against the (leading) cache and MUST
        // enqueue its own write.
        await kit.Chat.ToggleAsync();
        await kit.Chat.ToggleAsync();

        // Both flips are ISSUED (the cache led the pipeline), yet only the
        // first write reached the service so far: the second still waits
        // behind the parked one in the serialized queue.
        invokedWrites.Should().ContainSingle()
            .Which.Should().BeFalse("the second write waits behind the parked first one");

        firstWriteReleased.TrySetResult();

        var winner = await Task.WhenAny(secondWriteInvoked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(secondWriteInvoked.Task, "the concatenated writes drain in issuance order");

        // Assert: both writes are in the file, in ISSUE order — hide then
        // show — so a restart restores the property's final value.
        invokedWrites.Should().Equal(false, true);
        kit.Chat.IsAiChatVisible.Should().BeTrue("the panel carries the user's final choice");
    }

    /// <summary>
    /// One failed persistence write does not terminate the pipeline: the
    /// per-write catch keeps the concatenated stream serving later flips,
    /// and the failure is exactly one logged entry.
    /// </summary>
    [Fact]
    public async Task TwoToggles_WhenTheFirstWriteFails_TheSecondStillPersists()
    {
        var (chat, _, settings) = CreateChatWithChanges(UiSettingsKeys.DefaultShowAiPanel);
        await chat.InitializeAsync();

        var writeAttempts = 0;
        settings
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(_ => ++writeAttempts == 1
                ? Task.FromException(new InvalidOperationException("disk hiccup"))
                : Task.CompletedTask);

        await chat.ToggleAsync();
        await chat.ToggleAsync();

        chat.IsAiChatVisible.Should().BeTrue("the panel carries the user's final choice");
        await settings.Received(2).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            Arg.Any<bool>());
    }

    #endregion

    #region Send Pipeline

    /// <summary>
    /// An answered send: the user turn lands first with its localizer label,
    /// the answer appends as an assistant turn, and the accepted message
    /// yields the input to the answer (not kept waiting for it).
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AnAnsweredRequest_AppendsTheAssistantTurnAndClearsTheInput()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("The workspace frame is a display fact, not a trading signal.");
        kit.Chat.AiChatInput = "How is the market?";

        // Act
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        // Assert
        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "The workspace frame is a display fact, not a trading signal."));
        kit.Chat.ChatMessages[0].Role.Should().Be("AI.Chat.RoleUser");
        kit.Chat.ChatMessages[1].Role.Should().Be("AI.Chat.RoleAssistant");
        kit.Chat.AiChatInput.Should().BeEmpty("the accepted message yields the input to the answer");
        kit.Chat.IsBusy.Should().BeFalse("the send finished before the assertion");
    }

    /// <summary>
    /// The provider receives a composed workspace prompt (the assistant's
    /// identity in ZettaTrade, the current exchange) and exactly the session
    /// history: the user turn rides, nothing invented does.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_TheFirstSend_ComposesTheWorkspacePromptAndSendsTheUserTurn()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        var prompts = new List<string>();
        var histories = new List<IReadOnlyList<ChatTurn>>();
        kit.Completion
            .CompleteAsync(
                Arg.Do<string>(prompts.Add),
                Arg.Do<IReadOnlyList<ChatTurn>>(histories.Add),
                Arg.Any<CancellationToken>())
            .Returns("placeholder answer");
        kit.Chat.AiChatInput = "How is the market?";

        // Act
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        // Assert
        prompts.Should().ContainSingle()
            .Which.Should().Contain("ZettaTrade", "the identity line states the assistant's home")
                .And.Contain(ChatTests.IdleExchangeName, "the current exchange rides the prompt");
        histories.Should().ContainSingle();
        histories[0].Should().Equal(new ChatTurn(ChatTurn.UserRole, "How is the market?"));
    }

    /// <summary>
    /// The later send rides the honest session history: the answered first
    /// exchange (both turns) and the new question go to the model, while an
    /// in-chat error line from an unavailable assistant stays out — it is
    /// rendered for the trader, never sent as conversation.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_TheLaterExchange_RidesTheHistorySansErrorLines()
    {
        // Arrange: each send reaches the provider in order; the plan scripts
        // one answered exchange, an unavailable assistant, then a recovery
        var kit = ChatTests.CreateKit();
        var prompts = new List<string>();
        var histories = new List<IReadOnlyList<ChatTurn>>();
        var sends = 0;
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.Add(call.ArgAt<string>(0));
                histories.Add(call.ArgAt<IReadOnlyList<ChatTurn>>(1));

                return sends++ switch
                {
                    0 => Task.FromResult("first answer"),
                    1 => Task.FromException<string>(new HttpRequestException("the local model server is off")),
                    _ => Task.FromResult("third answer"),
                };
            });

        kit.Chat.AiChatInput = "first question";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.AiChatInput = "second question";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.AiChatInput = "third question";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        // Assert
        prompts.Should().HaveCount(3);
        histories.Should().HaveCount(3);

        // The second question's user turn stays in the honest history even
        // though its answer failed; only the error line stays out
        histories[2].Should().Equal(
            new ChatTurn(ChatTurn.UserRole, "first question"),
            new ChatTurn(ChatTurn.AssistantRole, "first answer"),
            new ChatTurn(ChatTurn.UserRole, "second question"),
            new ChatTurn(ChatTurn.UserRole, "third question"));
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(
                new[]
                {
                    ChatMessageKind.UserTurn,
                    ChatMessageKind.AssistantTurn,
                    ChatMessageKind.UserTurn,
                    ChatMessageKind.ErrorLine,
                    ChatMessageKind.UserTurn,
                    ChatMessageKind.AssistantTurn,
                },
                "the unavailable send keeps its question, explains itself, and the recovery answers the next ask");
    }

    /// <summary>
    /// An unavailable assistant reports honestly: the user turn stays (the
    /// trader sees what was asked), the in-chat error line carries the
    /// localized failure text with the underlying reason, the input cleared
    /// (the request was accepted), and the bus went idle again.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AnUnavailableAssistant_KeepsTheUserTurnAndReportsAnHonestErrorLine()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("the local model server is off")));
        kit.Localization
            .GetString("AI.Chat.Error", "the local model server is off")
            .Returns("The assistant is unavailable: the local model server is off");
        kit.Chat.AiChatInput = "How is the market?";

        // Act
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        // Assert
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn, ChatMessageKind.ErrorLine);
        kit.Chat.ChatMessages[1].Content
            .Should().Be("The assistant is unavailable: the local model server is off");
        kit.Chat.AiChatInput.Should().BeEmpty("the failed request was still accepted");
        kit.Chat.IsBusy.Should().BeFalse("the failure finished before the assertion");
    }

    /// <summary>
    /// An abandoned request (the send gave up before its answer — a shutdown
    /// or a canceled request) leaves the accepted user turn in place with no
    /// fabricated answer and no error line, and the bus goes idle again.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AbandonedRequest_KeepsTheTurnWithoutAnAnswerOrAnErrorLine()
    {
        // Arrange
        using var abandonment = new CancellationTokenSource();
        abandonment.Cancel();
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<string>(abandonment.Token));
        kit.Chat.AiChatInput = "How is the market?";

        // Act
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        // Assert
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn);
        kit.Chat.AiChatInput.Should().BeEmpty("the accepted request still cleared its input");
        kit.Chat.IsBusy.Should().BeFalse("the abandoned request finished before the assertion");
    }

    /// <summary>
    /// One assistant request at a time: while the first request is in flight
    /// the send is locked, so a fast second send cannot race a duplicate
    /// request past the guard — the command refuses the invocation (a vetoed
    /// error) or drops it (a no-op), and never a second completion.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AnotherRequestInFlight_CannotDoubleSend()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        kit.Chat.AiChatInput = "first question";

        var parked = kit.Chat
            .WhenAnyValue(x => x.IsBusy)
            .Where(busy => busy)
            .FirstAsync()
            .Timeout(SendWaitBudget)
            .ToTask();
        var firstDone = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstWatcher = kit.Chat.SendTextCommand.Execute(RxVoid.Default).Subscribe(
            value => firstDone.TrySetResult(value),
            exception => firstDone.SetException(exception),
            () => firstDone.TrySetResult(RxVoid.Default));
        await parked;

        // Act: the second send while parked — refused, never raced
        using var secondWatcher = kit.Chat.SendTextCommand.Execute(RxVoid.Default).Subscribe(
            _ => { },
            _ => { },
            () => { });

        pending.TrySetResult("the only answer");
        await firstDone.Task;

        // Assert
        await kit.Completion.Received(1).CompleteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChatTurn>>(),
            Arg.Any<CancellationToken>());
        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "first question"),
                (ChatMessageKind.AssistantTurn, "the only answer"));
        kit.Chat.IsBusy.Should().BeFalse();
        kit.Chat.AiChatInput.Should().BeEmpty();
    }

    /// <summary>
    /// No send without text: the empty box and the whitespace-only box keep
    /// the send locked, and typing a real message unlocks it.
    /// </summary>
    [Fact]
    public void SendTextCommand_WithoutUsableText_TheSendStaysLocked()
    {
        // Arrange: the guard emits its current state at subscribe, so one
        // subscription reads the standing lock as the input changes
        var kit = ChatTests.CreateKit();
        bool? unlocked = null;
        using var watch = kit.Chat.SendTextCommand.CanExecute.Subscribe(guard => unlocked = guard);

        unlocked.Should().BeFalse("no text, no send");

        // Act: whitespace is not a message either
        kit.Chat.AiChatInput = "   ";

        // Assert
        unlocked.Should().BeFalse("only whitespace, still no send");

        // Act: a real message unlocks the send
        kit.Chat.AiChatInput = "How is the market?";

        // Assert
        unlocked.Should().BeTrue();
    }

    #endregion

    #region Stop (T-158)

    /// <summary>
    /// The Stop affordance offers itself only while a reply runs: with
    /// nothing in flight it stays locked — never a dead button, and the
    /// raced click after the reply settled is a no-op by the guard.
    /// </summary>
    [Fact]
    public void StopCommand_WhileIdle_StaysLocked()
    {
        // Arrange: the guard emits its current state at subscribe
        var kit = ChatTests.CreateKit();
        bool? stoppable = null;
        using var watch = kit.Chat.StopCommand.CanExecute.Subscribe(guard => stoppable = guard);

        // Assert
        stoppable.Should().BeFalse("no request in flight, no stop — the button never presents itself dead");
    }

    /// <summary>
    /// The Stop click cancels the in-flight reply's source: the request
    /// rides one cancellation token, the stop flags it, and the canceled
    /// request settles with the accepted turn in the chat (no answer, no
    /// error line), the bus idle and the stop locked again — the next
    /// send (not a stop) is what a trader can do with a silent chat.
    /// </summary>
    [Fact]
    public async Task StopCommand_WhileInFlight_CancelsTheRequestAndSettlesTheStop()
    {
        // Arrange: a request parked mid-flight, its cancellation token captured
        var kit = ChatTests.CreateKit();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var histories = new List<IReadOnlyList<ChatTurn>>();
        var captured = new List<CancellationToken>();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Do<IReadOnlyList<ChatTurn>>(histories.Add),
                Arg.Do<CancellationToken>(captured.Add))
            .Returns(pending.Task);
        kit.Chat.AiChatInput = "How is the market?";

        var parked = kit.Chat
            .WhenAnyValue(x => x.IsBusy)
            .Where(busy => busy)
            .FirstAsync()
            .Timeout(SendWaitBudget)
            .ToTask();
        var firstDone = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstWatcher = kit.Chat.SendTextCommand.Execute(RxVoid.Default).Subscribe(
            value => firstDone.TrySetResult(value),
            exception => firstDone.SetException(exception),
            () => firstDone.TrySetResult(RxVoid.Default));
        await parked;

        // Act: the Stop click while the reply runs
        await kit.Chat.StopCommand.Execute(RxVoid.Default);

        captured.Should().ContainSingle("the request rides one cancellation token");
        captured[0].IsCancellationRequested.Should().BeTrue("the stop cancels the in-flight source");

        // The stopped request settles canceled
        pending.TrySetCanceled();
        await firstDone.Task;

        // Assert: the turn stays, nothing fabricated follows the stop, and
        // the affordance locks again the moment nothing is in flight
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(new[] { ChatMessageKind.UserTurn }, "the accepted turn stays, no answer and no error line follows a stop");
        kit.Chat.IsBusy.Should().BeFalse("the stopped request settled and the bus went idle");

        bool? stoppable = null;
        using var watch = kit.Chat.StopCommand.CanExecute.Subscribe(guard => stoppable = guard);
        stoppable.Should().BeFalse("the stop locked again — a late click is a no-op, not a dead button");
    }

    #endregion

    #region Trade Review Handoff (T-255)

    /// <summary>
    /// An idle chat takes the composed review as a plain user turn: the
    /// panel shows, the input row clears, the provider answers through
    /// the standard pipeline — the review is a user turn, nothing more.
    /// </summary>
    [Fact]
    public async Task SubmitReviewAsync_AnIdleChat_LandsThePromptAsAUserTurn()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("the review's answer");

        // Act
        await kit.Chat.SubmitReviewAsync("Review the following closed trade from my trading journal.");

        // Assert
        kit.Chat.IsAiChatVisible.Should().BeTrue("the review shows the panel it lands in");
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn, ChatMessageKind.AssistantTurn);
        kit.Chat.ChatMessages[0].Content
            .Should().Be("Review the following closed trade from my trading journal.");
        kit.Chat.AiChatInput.Should().BeEmpty("the accepted send cleared the input row");
        kit.Chat.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// While a reply is in flight the review parks its prompt in the input
    /// row (the user sends it when the answer settles) — one assistant
    /// request at a time, never a queue jump and never a second turn.
    /// </summary>
    [Fact]
    public async Task SubmitReviewAsync_AnInFlightReply_ParksThePromptInTheInput()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(gate.Task);
        kit.Chat.AiChatInput = "typed while waiting";
        var parked = kit.Chat
            .WhenAnyValue(x => x.IsBusy)
            .Where(busy => busy)
            .FirstAsync()
            .Timeout(SendWaitBudget)
            .ToTask();
        var settled = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sendWatcher = kit.Chat.SendTextCommand.Execute(RxVoid.Default).Subscribe(
            value => settled.TrySetResult(value),
            exception => settled.SetException(exception),
            () => settled.TrySetResult(RxVoid.Default));
        await parked;
        kit.Chat.IsBusy.Should().BeTrue("the send is parked at the hanging completion");

        // Act
        await kit.Chat.SubmitReviewAsync("Review this trade.");

        // Assert
        kit.Chat.AiChatInput.Should()
            .Be("Review this trade.", "the prompt parks in the input row while the reply is in flight");
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal([ChatMessageKind.UserTurn], "no second turn lands while a reply is in flight");

        gate.SetResult("settled");
        await settled.Task;
        kit.Chat.IsBusy.Should().BeFalse();
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn, ChatMessageKind.AssistantTurn);
    }

    /// <summary>
    /// The provider-failure matrix through the review route: an
    /// unavailable assistant reports honestly — the user turn stays, the
    /// in-chat error line carries the localized failure text, the bus
    /// goes idle again. A failure is a localized error line, never
    /// silence.
    /// </summary>
    [Fact]
    public async Task SubmitReviewAsync_AnUnavailableAssistant_ReportsAnHonestErrorLine()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("the local model server is off")));
        kit.Localization
            .GetString("AI.Chat.Error", "the local model server is off")
            .Returns("The assistant is unavailable: the local model server is off");

        // Act
        await kit.Chat.SubmitReviewAsync("Review this trade.");

        // Assert
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn, ChatMessageKind.ErrorLine);
        kit.Chat.ChatMessages[1].Content
            .Should().Be("The assistant is unavailable: the local model server is off");
        kit.Chat.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// The read-only guarantee (T-255): the review sends exactly one
    /// request through the provider seam and nothing follows it — no
    /// auto-posted continuation, no second ask. The output lands in the
    /// chat only.
    /// </summary>
    [Fact]
    public async Task SubmitReviewAsync_SendsExactlyOneRequest_NeverAnAutoPostedFollowUp()
    {
        // Arrange
        var kit = ChatTests.CreateKit();
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("the review's answer");

        // Act
        await kit.Chat.SubmitReviewAsync("Review this trade.");

        // Assert
        await kit.Completion.Received(1).CompleteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChatTurn>>(),
            Arg.Any<CancellationToken>());
        kit.Chat.ChatMessages
            .Select(message => message.Kind)
            .Should().Equal(ChatMessageKind.UserTurn, ChatMessageKind.AssistantTurn);
    }

    #endregion

    #region Factories

    /// <summary>Create a chat whose persisted read returns <paramref name="persistedValue"/>.</summary>
    private static ChatViewModel CreateChat(
        out ISettingsService settings,
        bool persistedValue)
    {
        var kit = ChatTests.CreateKit(persistedValue);

        settings = kit.Settings;

        return kit.Chat;
    }

    /// <summary>A chat with its changes stream and settings substitute at hand.</summary>
    private static (ChatViewModel Chat, Subject<SettingsChange> Changes, ISettingsService Settings)
        CreateChatWithChanges(bool persistedValue)
    {
        var kit = ChatTests.CreateKit(persistedValue);

        return (kit.Chat, kit.Changes, kit.Settings);
    }

    #endregion
}
