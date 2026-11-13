namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The persistent chat history (T-206): the startup restore fills the picker
/// newest-first and replays the newest transcript, the empty store stands
/// empty until the first send creates the conversation, every send persists
/// its turns (the user turn with the auto-title, the one-shot reply, the
/// streamed settlement's prefix, the honest error line), the picker swap
/// replays the picked transcript behind its guards, the fresh-conversation
/// command opens a record the next send writes into, and the export builds
/// the golden document and writes it UTF-8 without a BOM. The store is an
/// in-memory script the test reads back.
/// </summary>
public sealed class ChatViewModelHistoryTests
{
    /// <summary>
    /// How long a test waits for the send pipeline to reach its next
    /// observable signal before calling the arrangement broken (never hit in
    /// a healthy pipeline; keeps a stalled test diagnosable).
    /// </summary>
    private static readonly TimeSpan SendWaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The witness budget: how long a test polls for the mid-flight prefix
    /// before calling the stream stalled (the scripted beat lands well
    /// within; the budget only fires on a broken pipeline).
    /// </summary>
    private static readonly TimeSpan WitnessBudget = TimeSpan.FromSeconds(5);

    /// <summary>The standing spoken moments the seeded transcripts ride.</summary>
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    #region Fixture

    /// <summary>
    /// A scripted streaming provider: the deltas arrive with a real beat
    /// between them (the cancellation token rides the beat, so a Stop cut
    /// unwinds the iterator the way a real provider does), and an optional
    /// fault fires after a scripted delta count.
    /// </summary>
    private sealed class ScriptedStreamingCompletion : IStreamingChatCompletion
    {
        private readonly string[] _deltas;

        /// <summary>The pause between two deltas (the cancel lands here).</summary>
        internal TimeSpan Beat { get; set; } = TimeSpan.FromMilliseconds(120);

        /// <summary>The fault to throw (the unusable-reply shape by default).</summary>
        internal Exception Fault { get; set; } = new HttpRequestException("the endpoint broke");

        /// <summary>After how many deltas the fault fires (none by default).</summary>
        internal int FaultAfterDeltas { get; set; } = int.MaxValue;

        /// <summary>How often the streamed path was taken (the fallback facts read it).</summary>
        internal int StreamCalls { get; private set; }

        internal ScriptedStreamingCompletion(params string[] deltas)
        {
            _deltas = deltas;
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            IReadOnlyList<ChatTurn> turns,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCalls++;
            if (FaultAfterDeltas == 0)
            {
                throw Fault;
            }

            for (var index = 0; index < _deltas.Length; index++)
            {
                if (index == FaultAfterDeltas)
                {
                    throw Fault;
                }

                await Task.Delay(Beat, ct);
                yield return _deltas[index];
            }
        }
    }

    /// <summary>
    /// An in-memory history store the test scripts and reads back: the
    /// conversations and transcripts live in plain collections, the faults
    /// inject at the seams the VM's swallow-paths cover, and the call
    /// counters witness the guards (a no-op that never loads, a rename that
    /// fires once).
    /// </summary>
    private sealed class ScriptedChatHistoryStore : IChatHistoryStore
    {
        private readonly List<ChatConversation> _conversations = new();
        private readonly Dictionary<Guid, List<ChatHistoryMessage>> _transcripts = new();

        internal Exception? ListFault { get; set; }
        internal Exception? CreateFault { get; set; }
        internal Exception? AppendFault { get; set; }
        internal Exception? LoadMessagesFault { get; set; }

        internal int CreateCalls { get; private set; }
        internal int AppendCalls { get; private set; }
        internal int RenameCalls { get; private set; }
        internal int LoadCalls { get; private set; }

        /// <summary>Seed one conversation (newest first is the caller's order).</summary>
        internal ChatConversation Seed(
            string? title,
            DateTimeOffset updatedUtc,
            params (string Kind, string Content, DateTimeOffset TimestampUtc)[] messages)
        {
            var conversation = new ChatConversation(Guid.NewGuid(), title, BaseTime, updatedUtc);
            _conversations.Add(conversation);
            _transcripts[conversation.Id] = messages
                .Select(message => new ChatHistoryMessage(message.Kind, message.Content, message.TimestampUtc))
                .ToList();
            return conversation;
        }

        internal ChatConversation Conversation(int index) => _conversations[index];

        internal IReadOnlyList<ChatHistoryMessage> Transcript(Guid conversationId) => _transcripts[conversationId];

        public async Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(
            CancellationToken cancellationToken = default)
        {
            if (ListFault is not null)
            {
                throw ListFault;
            }

            // Newest update first, in the seeded order on a tie (the tests
            // seed distinct moments, so the stable sort is a straight replay).
            return _conversations
                .OrderByDescending(conversation => conversation.UpdatedUtc)
                .ToList();
        }

        public async Task<IReadOnlyList<ChatHistoryMessage>> LoadMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            if (LoadMessagesFault is not null)
            {
                throw LoadMessagesFault;
            }

            return _transcripts.TryGetValue(conversationId, out var messages)
                ? messages.ToList()
                : [];
        }

        public async Task<ChatConversation> CreateConversationAsync(CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (CreateFault is not null)
            {
                throw CreateFault;
            }

            var now = DateTimeOffset.UtcNow;
            var conversation = new ChatConversation(Guid.NewGuid(), null, now, now);
            _conversations.Add(conversation);
            _transcripts[conversation.Id] = new List<ChatHistoryMessage>();
            return conversation;
        }

        public async Task AppendMessageAsync(
            Guid conversationId,
            ChatHistoryMessage message,
            CancellationToken cancellationToken = default)
        {
            if (AppendFault is not null)
            {
                throw AppendFault;
            }

            var index = _conversations.FindIndex(conversation => conversation.Id == conversationId);
            if (index < 0)
            {
                return; // the store's honest drop
            }

            AppendCalls++;
            _transcripts[conversationId].Add(message);
            _conversations[index] = _conversations[index] with { UpdatedUtc = DateTimeOffset.UtcNow };
        }

        public async Task RenameConversationAsync(
            Guid conversationId,
            string? title,
            CancellationToken cancellationToken = default)
        {
            RenameCalls++;
            var index = _conversations.FindIndex(conversation => conversation.Id == conversationId);
            if (index >= 0)
            {
                _conversations[index] = _conversations[index] with { Title = title };
            }
        }
    }

    /// <summary>Poll until the condition holds, then give up with the reason.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + WitnessBudget;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The witness never saw the state: {because}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Fire the send and get the completion witness back (fire-and-forget).</summary>
    private static (IDisposable Watcher, Task Settled) FireAndForget(ChatViewModel chat)
    {
        var settled = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = chat.SendTextCommand.Execute(RxVoid.Default).Subscribe(
            value => settled.TrySetResult(value),
            exception => settled.SetException(exception),
            () => settled.TrySetResult(RxVoid.Default));

        return (watcher, settled.Task);
    }

    /// <summary>The kit's one-shot completion answers a fixed reply.</summary>
    private static void OneShotAnswers(ChatKit kit, string reply) => kit.Completion
        .CompleteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChatTurn>>(),
            Arg.Any<CancellationToken>())
        .Returns(reply);

    #endregion

    #region Restore

    /// <summary>
    /// The startup restore fills the picker newest-first and replays the
    /// newest conversation's transcript — kinds, contents, roles and the
    /// spoken moments (the store's UTC instants land local).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePicker_AndTheNewestTranscript()
    {
        var store = new ScriptedChatHistoryStore();
        store.Seed(
            "older chat",
            BaseTime,
            (ChatHistoryMessage.UserKind, "old question", BaseTime));
        var newer = store.Seed(
            null,
            BaseTime.AddHours(2),
            (ChatHistoryMessage.UserKind, "hello", BaseTime.AddHours(2)),
            (ChatHistoryMessage.AssistantKind, "hi there", BaseTime.AddHours(2).AddMinutes(1)),
            (ChatHistoryMessage.ErrorKind, "boom", BaseTime.AddHours(2).AddMinutes(2)));
        var kit = ChatTests.CreateKit(history: store);

        await kit.Chat.InitializeAsync();

        kit.Chat.Conversations.Select(conversation => conversation.Id)
            .Should().Equal([newer.Id, store.Conversation(0).Id],
                "the picker reads the store's newest-first answer.");
        kit.Chat.SelectedConversation.Should().NotBeNull();
        kit.Chat.SelectedConversation!.Id.Should().Be(newer.Id,
            "the newest conversation is on display after the restore.");
        kit.Chat.ChatMessages.Select(message => (message.Kind, message.Role, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "AI.Chat.RoleUser", "hello"),
                (ChatMessageKind.AssistantTurn, "AI.Chat.RoleAssistant", "hi there"),
                (ChatMessageKind.ErrorLine, "AI.Chat.RoleAssistant", "boom"));
        kit.Chat.ChatMessages.Select(message => message.Timestamp)
            .Should().Equal(
                [
                    BaseTime.AddHours(2).ToLocalTime(),
                    BaseTime.AddHours(2).AddMinutes(1).ToLocalTime(),
                    BaseTime.AddHours(2).AddMinutes(2).ToLocalTime(),
                ],
                "the stored UTC instants land as the local moments the UI shows.");
        kit.Chat.SelectedConversation!.HasStoredTitle.Should().BeFalse(
            "an untitled conversation renders its localized default.");
        kit.Chat.SelectedConversation!.DisplayTitle.Should().Be("AI.Chat.ConversationDefaultTitle");
    }

    /// <summary>
    /// The empty store stands empty — no picker row, no transcript — until
    /// the first send opens the conversation lazily: the row enters the
    /// picker, becomes the selection, the turn auto-titles it and the
    /// transcript of record grows.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_AnEmptyStore_StandsEmptyUntilTheFirstSend()
    {
        var store = new ScriptedChatHistoryStore();
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "pong");

        await kit.Chat.InitializeAsync();
        kit.Chat.Conversations.Should().BeEmpty("a fresh installation's picker is empty.");
        kit.Chat.SelectedConversation.Should().BeNull("nothing is on display.");
        kit.Chat.ChatMessages.Should().BeEmpty();

        kit.Chat.AiChatInput = "hi there";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.CreateCalls.Should().Be(1, "the first send opened the conversation.");
        store.Conversation(0).Title.Should().Be("hi there",
            "the first user turn auto-titled the fresh conversation.");
        store.Transcript(store.Conversation(0).Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatHistoryMessage.UserKind, "hi there"),
                (ChatHistoryMessage.AssistantKind, "pong"));
        kit.Chat.Conversations.Should().ContainSingle("the picker row entered with the conversation.");
        kit.Chat.SelectedConversation!.Id.Should().Be(store.Conversation(0).Id,
            "the lazily-created conversation is on display.");
    }

    /// <summary>
    /// A faulted picker read leaves the panel standing — empty picker, empty
    /// chat, logged and swallowed — and the next send still opens a fresh
    /// conversation (the store heals at the next turn boundary).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_AFaultedListRead_LeavesThePanelStanding()
    {
        var store = new ScriptedChatHistoryStore
        {
            ListFault = new HttpRequestException("the history database is unreadable"),
        };
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "pong");

        await kit.Chat.InitializeAsync();

        kit.Chat.Conversations.Should().BeEmpty("the faulted read stood the panel down.");
        kit.Chat.SelectedConversation.Should().BeNull();
        kit.Chat.ChatMessages.Should().BeEmpty();

        kit.Chat.AiChatInput = "hello";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.CreateCalls.Should().Be(1, "the next send opened a fresh conversation.");
        kit.Chat.Conversations.Should().ContainSingle("the fresh conversation entered the picker.");
    }

    #endregion

    #region Persist

    /// <summary>
    /// A send persists both its turns: the user turn (auto-titling the
    /// conversation) and the one-shot reply, in the order they were spoken.
    /// </summary>
    [Fact]
    public async Task Send_PersistsTheUserTurn_TheReply_AndAutoTitlesTheConversation()
    {
        var store = new ScriptedChatHistoryStore();
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "the one-shot answer");

        kit.Chat.AiChatInput = "How is the market?";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Conversation(0).Title.Should().Be("How is the market?");
        store.Transcript(store.Conversation(0).Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatHistoryMessage.UserKind, "How is the market?"),
                (ChatHistoryMessage.AssistantKind, "the one-shot answer"));
        store.AppendCalls.Should().Be(2, "one append per turn.");
    }

    /// <summary>
    /// The auto-title fires once per conversation: the FIRST titled turn's
    /// input becomes the title (trimmed, 48 characters at most with the
    /// ellipsis), later turns never retitle.
    /// </summary>
    [Fact]
    public async Task Send_TheAutoTitle_FiresOnce_AndTruncatesTheFirstTurn()
    {
        var store = new ScriptedChatHistoryStore();
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "pong");

        var longQuestion = new string('x', 60);
        kit.Chat.AiChatInput = longQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        var expectedTitle = new string('x', 48) + "…";
        store.Conversation(0).Title.Should().Be(expectedTitle,
            "the first turn's input truncates at 48 characters plus the ellipsis.");

        kit.Chat.AiChatInput = "a later, shorter question";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Conversation(0).Title.Should().Be(expectedTitle, "the auto-title never fires twice.");
        store.RenameCalls.Should().Be(1, "one rename per conversation.");
        store.Transcript(store.Conversation(0).Id)
            .Should().HaveCount(4, "both turns of both sends persisted.");
    }

    /// <summary>
    /// A Stop cut mid-flight persists the arrived prefix (the deltas are the
    /// model's real output) — no error line follows it into the record.
    /// </summary>
    [Fact]
    public async Task StopOnTheStreamedReply_PersistsTheArrivedPrefix()
    {
        var store = new ScriptedChatHistoryStore();
        var fake = new ScriptedStreamingCompletion("alpha", " beta", " gamma");
        var kit = ChatTests.CreateKit(streaming: fake, history: store);
        kit.StreamPerSettings(true);
        kit.Chat.AiChatInput = "How is the market?";

        var (watcher, settled) = FireAndForget(kit.Chat);
        using (watcher)
        {
            await WaitForAsync(
                () => kit.Chat.ChatMessages.Count == 2 &&
                      kit.Chat.ChatMessages[1].Content == "alpha",
                "the prefix rendered before the cut");

            kit.Chat.StopCommand.Execute(RxVoid.Default).Subscribe(_ => { }, _ => { }, () => { });

            await settled.WaitAsync(SendWaitBudget);
        }

        store.Transcript(store.Conversation(0).Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "How is the market?"),
                    (ChatHistoryMessage.AssistantKind, "alpha"),
                ],
                "the stopped stream's arrived prefix is the record's answer.");
        store.Transcript(store.Conversation(0).Id)
            .Should().NotContain(message => message.Kind == ChatHistoryMessage.ErrorKind,
                "a stop carries no error line into the record.");
    }

    /// <summary>
    /// A mid-flight stream fault persists the arrived prefix AND the error
    /// line — the record carries exactly what the chat showed.
    /// </summary>
    [Fact]
    public async Task AMidFlightStreamFault_PersistsThePrefix_AndTheErrorLine()
    {
        var store = new ScriptedChatHistoryStore();
        var fake = new ScriptedStreamingCompletion("alpha", " beta", " gamma")
        {
            FaultAfterDeltas = 2,
        };
        var kit = ChatTests.CreateKit(streaming: fake, history: store);
        kit.StreamPerSettings(true);
        kit.Localization
            .GetString("AI.Chat.Error", Arg.Any<string>())
            .Returns("The assistant is unavailable: the endpoint broke");
        kit.Chat.AiChatInput = "How is the market?";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Transcript(store.Conversation(0).Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "How is the market?"),
                    (ChatHistoryMessage.AssistantKind, "alpha beta"),
                    (ChatHistoryMessage.ErrorKind, "The assistant is unavailable: the endpoint broke"),
                ]);
    }

    /// <summary>
    /// A faulted one-shot send persists the user turn and the honest error
    /// line — the unavailable assistant's record is as honest as its chat.
    /// </summary>
    [Fact]
    public async Task AFaultedOneShotSend_PersistsTheErrorLine()
    {
        var store = new ScriptedChatHistoryStore();
        var kit = ChatTests.CreateKit(history: store);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("the endpoint broke")));
        kit.Localization
            .GetString("AI.Chat.Error", Arg.Any<string>())
            .Returns("The assistant is unavailable: the endpoint broke");

        kit.Chat.AiChatInput = "How is the market?";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Transcript(store.Conversation(0).Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "How is the market?"),
                    (ChatHistoryMessage.ErrorKind, "The assistant is unavailable: the endpoint broke"),
                ]);
    }

    #endregion

    #region Switching

    /// <summary>
    /// The picker swap replays the picked transcript and the record follows:
    /// the next send writes into the switched-to conversation (whose stored
    /// title stands — no auto-title re-fire).
    /// </summary>
    [Fact]
    public async Task SelectConversation_SwapsTheTranscript_AndTheRecordFollows()
    {
        var store = new ScriptedChatHistoryStore();
        var older = store.Seed(
            "older chat",
            BaseTime,
            (ChatHistoryMessage.UserKind, "old question", BaseTime));
        store.Seed(
            null,
            BaseTime.AddHours(2),
            (ChatHistoryMessage.UserKind, "hi", BaseTime.AddHours(2)));
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "the one-shot answer");
        await kit.Chat.InitializeAsync();

        await kit.Chat.SelectConversationAsync(kit.Chat.Conversations.Last());

        kit.Chat.SelectedConversation!.Id.Should().Be(older.Id, "the pick is on display.");
        kit.Chat.ChatMessages.Select(message => message.Content)
            .Should().Equal(["old question"], "the picked transcript replays.");

        kit.Chat.AiChatInput = "follow up";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Transcript(older.Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "old question"),
                    (ChatHistoryMessage.UserKind, "follow up"),
                    (ChatHistoryMessage.AssistantKind, "the one-shot answer"),
                ],
                "the record follows the conversation on display.");
        store.RenameCalls.Should().Be(0,
            "the stored title stands — the auto-title never re-fires on a titled conversation.");
        store.Transcript(store.Conversation(1).Id)
            .Should().ContainSingle("the sibling conversation is nobody's business.");
    }

    /// <summary>
    /// The conversation already on display never reloads: the swap's
    /// Id-guard absorbs the pick (and the OneWay binding's re-entrant echo).
    /// </summary>
    [Fact]
    public async Task SelectConversation_TheSameConversationAgain_IsANoOp()
    {
        var store = new ScriptedChatHistoryStore();
        store.Seed(
            "only chat",
            BaseTime,
            (ChatHistoryMessage.UserKind, "hello", BaseTime));
        var kit = ChatTests.CreateKit(history: store);
        await kit.Chat.InitializeAsync();

        var loadsAfterRestore = store.LoadCalls;
        await kit.Chat.SelectConversationAsync(kit.Chat.Conversations[0]);

        store.LoadCalls.Should().Be(loadsAfterRestore,
            "the displayed conversation is the Id-guard's no-op.");
        kit.Chat.ChatMessages.Select(message => message.Content)
            .Should().Equal(["hello"], "the transcript never flickers.");
    }

    /// <summary>
    /// While a reply runs the transcript is owned: the swap stays inert, the
    /// in-flight prefix keeps its bubble and the record stays where it was.
    /// </summary>
    [Fact]
    public async Task SelectConversation_WhileBusy_IsInert()
    {
        var store = new ScriptedChatHistoryStore();
        store.Seed(
            "other chat",
            BaseTime,
            (ChatHistoryMessage.UserKind, "other question", BaseTime));
        store.Seed(
            null,
            BaseTime.AddHours(2),
            (ChatHistoryMessage.UserKind, "earlier turn", BaseTime.AddHours(2)));
        var fake = new ScriptedStreamingCompletion("alpha", " beta", " gamma");
        var kit = ChatTests.CreateKit(streaming: fake, history: store);
        kit.StreamPerSettings(true);
        await kit.Chat.InitializeAsync();

        // The restore put the newest conversation on display (its seeded
        // transcript replayed); the older sibling is the swap attempt the
        // busy reply must refuse.
        var current = kit.Chat.Conversations[0];
        var other = kit.Chat.Conversations[1];
        kit.Chat.SelectedConversation!.Id.Should().Be(current.Id);
        kit.Chat.ChatMessages.Select(message => message.Content)
            .Should().Equal(["earlier turn"], "the restore replayed the displayed transcript.");
        store.LoadCalls.Should().Be(1, "the restore loaded the displayed transcript once.");

        kit.Chat.AiChatInput = "current question";
        var (watcher, settled) = FireAndForget(kit.Chat);
        using (watcher)
        {
            await WaitForAsync(
                () => kit.Chat.ChatMessages.Count == 3 &&
                      kit.Chat.ChatMessages[2].Content == "alpha",
                "the prefix rendered before the inert swap attempt");

            await kit.Chat.SelectConversationAsync(other);

            kit.Chat.SelectedConversation!.Id.Should().Be(current.Id,
                "the busy reply owns the transcript until it settles.");
            store.LoadCalls.Should().Be(1, "the inert swap never loaded the sibling.");
            kit.Chat.ChatMessages.Should().HaveCount(3,
                "the in-flight conversation stayed on display.");

            kit.Chat.StopCommand.Execute(RxVoid.Default).Subscribe(_ => { }, _ => { }, () => { });
            await settled.WaitAsync(SendWaitBudget);
        }

        store.Transcript(current.Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "earlier turn"),
                    (ChatHistoryMessage.UserKind, "current question"),
                    (ChatHistoryMessage.AssistantKind, "alpha"),
                ],
                "the settled reply recorded into the conversation it started in.");
    }

    #endregion

    #region New Conversation

    /// <summary>
    /// The fresh-conversation command opens a new record: it enters the
    /// picker's top, becomes the selection, the transcript clears — and the
    /// next send writes into the fresh record, never the old one.
    /// </summary>
    [Fact]
    public async Task NewConversation_OpensAFreshRecord_AndTheNextSendWritesThere()
    {
        var store = new ScriptedChatHistoryStore();
        var seeded = store.Seed(
            "previous chat",
            BaseTime,
            (ChatHistoryMessage.UserKind, "old question", BaseTime));
        var kit = ChatTests.CreateKit(history: store);
        OneShotAnswers(kit, "pong");
        await kit.Chat.InitializeAsync();

        await kit.Chat.NewConversationCommand.Execute(RxVoid.Default);

        store.CreateCalls.Should().Be(1, "the command opened one fresh record.");
        kit.Chat.Conversations.Should().HaveCount(2);
        kit.Chat.Conversations[0].Id.Should().NotBe(seeded.Id,
            "the fresh row entered at the picker's top.");
        kit.Chat.SelectedConversation!.Id.Should().Be(kit.Chat.Conversations[0].Id,
            "the fresh conversation is on display.");
        kit.Chat.ChatMessages.Should().BeEmpty("the fresh conversation's transcript is empty.");
        kit.Chat.SelectedConversation!.HasStoredTitle.Should().BeFalse(
            "a fresh record is untitled until its first message.");

        kit.Chat.AiChatInput = "new ground";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        store.Transcript(kit.Chat.Conversations[0].Id)
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                [
                    (ChatHistoryMessage.UserKind, "new ground"),
                    (ChatHistoryMessage.AssistantKind, "pong"),
                ],
                "the send wrote into the fresh record.");
        store.Transcript(seeded.Id)
            .Should().ContainSingle("the previous conversation is nobody's business.");
    }

    #endregion

    #region Export

    /// <summary>
    /// The export document's title line: the conversation's display title
    /// (the stored one when titled), then the transcript's labelled
    /// paragraphs — the exact text the format pins.
    /// </summary>
    [Fact]
    public async Task BuildExportMarkdown_IsTheGoldenDocument()
    {
        var store = new ScriptedChatHistoryStore();
        store.Seed("Golden script", BaseTime);
        var kit = ChatTests.CreateKit(history: store);
        await kit.Chat.InitializeAsync();

        kit.Chat.BuildExportMarkdown().Should().Be("# Golden script\n\n",
            "an empty transcript exports its title line alone.");
    }

    /// <summary>
    /// One labelled paragraph per row — the role label, the invariant-culture
    /// moment, the content — riding the display title of the storeless
    /// panel's default conversation.
    /// </summary>
    [Fact]
    public async Task BuildExportMarkdown_ContainsOneLabelledParagraphPerRow()
    {
        var kit = ChatTests.CreateKit();
        OneShotAnswers(kit, "pong");

        kit.Chat.AiChatInput = "hello";
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        var expected = "# AI.Chat.ConversationDefaultTitle\n\n" + string.Concat(
            kit.Chat.ChatMessages.Select(message =>
                $"**{message.Role}** ({message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})\n\n{message.Content}\n\n"));
        kit.Chat.BuildExportMarkdown().Should().Be(expected,
            "the golden format: title line, then one labelled paragraph per row.");
    }

    /// <summary>The export's suggested file name rides the chat stamp of the moment.</summary>
    [Fact]
    public void BuildExportFileName_RidesTheChatStamp()
    {
        ChatTests.CreateKit().Chat.BuildExportFileName()
            .Should().MatchRegex(@"^zettatrade-chat-\d{8}-\d{6}\.md$");
    }

    /// <summary>
    /// A picked path receives the document, UTF-8 with no byte-order mark —
    /// a fresh read of the file is the document byte for byte.
    /// </summary>
    [Fact]
    public async Task ExportToAsync_WritesTheTranscript_Utf8WithoutABom()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zettatrade-chat-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "chat.md");
            var kit = ChatTests.CreateKit();
            OneShotAnswers(kit, "pong");
            kit.Chat.AiChatInput = "hello";
            await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

            (await kit.Chat.ExportToAsync(path)).Should().BeTrue("the write succeeded.");

            var bytes = File.ReadAllBytes(path);
            Encoding.UTF8.GetString(bytes).Should().NotStartWith("\uFEFF",
                "the export writes UTF-8 with no byte-order mark.");
            Encoding.UTF8.GetString(bytes).Should().Be(kit.Chat.BuildExportMarkdown(),
                "the file's content is the document.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A failed export renders the in-chat error line and answers false —
    /// and the record stays untouched (a local operation's failure is not
    /// part of the conversation).
    /// </summary>
    [Fact]
    public async Task ExportToAsync_AFaultedWrite_RendersTheErrorLine_AndAnswersFalse()
    {
        var store = new ScriptedChatHistoryStore();
        var kit = ChatTests.CreateKit(history: store);
        kit.Localization
            .GetString("AI.Chat.ExportFailed", Arg.Any<string>())
            .Returns("The chat could not be exported");
        var directory = Path.Combine(Path.GetTempPath(), $"zettatrade-chat-export-missing-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "chat.md");

        (await kit.Chat.ExportToAsync(path)).Should().BeFalse("the write failed.");
        kit.Chat.ChatMessages
            .Should().ContainSingle(message => message.Kind == ChatMessageKind.ErrorLine &&
                message.Content == "The chat could not be exported");
        store.CreateCalls.Should().Be(0,
            "the export failure is nobody's record: no conversation opened, nothing appended.");
    }

    #endregion
}
