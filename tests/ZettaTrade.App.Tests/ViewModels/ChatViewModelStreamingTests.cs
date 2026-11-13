namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.App.ViewModels;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The streamed reply path (T-205): with the streaming key on and the
/// provider offering the stream, the deltas append into ONE assistant bubble
/// while the answer is still being written (the mid-flight prefix is
/// observable), a Stop cut keeps the arrived prefix with no error line, a
/// fault before the first delta renders the error line alone (never an
/// empty bubble), a mid-flight fault keeps the prefix and adds the error
/// line, and the fallback keeps the one-shot path verbatim — the key off, or
/// the capability missing, both ride the standing shape.
/// </summary>
public sealed class ChatViewModelStreamingTests
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

    #endregion

    #region Progressive Render

    /// <summary>
    /// A streamed reply renders progressively: the bubble exists with the
    /// first delta's content while the writer is still mid-flight (the
    /// content change raised — the bound text re-rendered), and the settled
    /// bubble carries the deltas' concatenation.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AStreamedReply_RendersTheBubbleProgressively()
    {
        var fake = new ScriptedStreamingCompletion("one", " two", " three");
        var kit = ChatTests.CreateKit(streaming: fake);
        kit.StreamPerSettings(true);
        kit.Chat.AiChatInput = "How is the market?";

        var (watcher, settled) = FireAndForget(kit.Chat);
        using (watcher)
        {
            await WaitForAsync(
                () => kit.Chat.ChatMessages.Count == 2 &&
                      kit.Chat.ChatMessages[1].Content == "one",
                "the first delta's prefix rendered mid-flight");

            await settled.WaitAsync(SendWaitBudget);
        }

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "one two three"));
        kit.Chat.IsBusy.Should().BeFalse("the send finished before the assertion");
        fake.StreamCalls.Should().Be(1, "the streamed path served the send");
        await kit.Completion.DidNotReceiveWithAnyArgs().CompleteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChatTurn>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The streamed preference is read at issue time: a settings flip between
    /// two sends changes exactly what the next send does (streamed, then the
    /// one-shot shape).
    /// </summary>
    [Fact]
    public async Task SendTextCommand_TheKeyFlipBetweenSends_ChangesTheNextSend()
    {
        var fake = new ScriptedStreamingCompletion("one", " two");
        var kit = ChatTests.CreateKit(streaming: fake);
        kit.StreamPerSettings(true);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("the one-shot answer");
        kit.Chat.AiChatInput = "first question";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "first question"),
                (ChatMessageKind.AssistantTurn, "one two"));

        kit.StreamPerSettings(false);
        kit.Chat.AiChatInput = "second question";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "first question"),
                (ChatMessageKind.AssistantTurn, "one two"),
                (ChatMessageKind.UserTurn, "second question"),
                (ChatMessageKind.AssistantTurn, "the one-shot answer"));
        fake.StreamCalls.Should().Be(1, "only the first send took the streamed path");
    }

    #endregion

    #region Stop

    /// <summary>
    /// A Stop cut mid-flight keeps the arrived prefix (the deltas are the
    /// model's real output) with no error line, and the bus goes idle — the
    /// T-158 semantics, the streamed prefix included.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AStopMidStream_KeepsTheArrivedPrefixWithoutAnErrorLine()
    {
        var fake = new ScriptedStreamingCompletion("alpha", " beta", " gamma");
        var kit = ChatTests.CreateKit(streaming: fake);
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

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "alpha"));
        kit.Chat.ChatMessages.Should()
            .NotContain(message => message.Kind == ChatMessageKind.ErrorLine);
        kit.Chat.IsBusy.Should().BeFalse("the stopped send unlocked the bus");
    }

    #endregion

    #region Fallback

    /// <summary>
    /// The fallback keeps the one-shot path verbatim: the key off sends the
    /// standing shape (one assistant turn with the whole reply), the
    /// streaming provider never called.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_TheKeyOff_RidesTheOneShotPathVerbatim()
    {
        var fake = new ScriptedStreamingCompletion("one", " two");
        var kit = ChatTests.CreateKit(streaming: fake);
        kit.StreamPerSettings(false);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("the one-shot answer");
        kit.Chat.AiChatInput = "How is the market?";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "the one-shot answer"));
        fake.StreamCalls.Should().Be(0, "the fallback never asked the stream");
    }

    /// <summary>
    /// The key cannot turn a missing capability on: without the streaming
    /// provider the chat rides the one-shot completion whatever the key says.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_WithoutAStreamingProvider_TheKeyCannotTurnThePathOn()
    {
        var kit = ChatTests.CreateKit(streaming: null);
        kit.StreamPerSettings(true);
        kit.Completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns("the one-shot answer");
        kit.Chat.AiChatInput = "How is the market?";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "the one-shot answer"));
    }

    #endregion

    #region Failures

    /// <summary>
    /// A stream that faults before the first delta renders the honest error
    /// line alone — never an empty assistant bubble.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AStreamedFaultBeforeTheFirstDelta_ReportsTheErrorLineOnly()
    {
        var fake = new ScriptedStreamingCompletion("one", " two")
        {
            FaultAfterDeltas = 0,
            Fault = new HttpRequestException("the local model server is off"),
        };
        var kit = ChatTests.CreateKit(streaming: fake);
        kit.StreamPerSettings(true);
        kit.Localization
            .GetString("AI.Chat.Error", "the local model server is off")
            .Returns("The assistant is unavailable: the local model server is off");
        kit.Chat.AiChatInput = "How is the market?";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.ErrorLine, "The assistant is unavailable: the local model server is off"));
        kit.Chat.IsBusy.Should().BeFalse("the faulted send unlocked the bus");
    }

    /// <summary>
    /// A stream that faults mid-flight keeps the arrived prefix (the model's
    /// real output) and adds the honest error line after it.
    /// </summary>
    [Fact]
    public async Task SendTextCommand_AStreamedFaultMidFlight_KeepsThePrefixAndReportsTheErrorLine()
    {
        var fake = new ScriptedStreamingCompletion("alpha", " beta")
        {
            FaultAfterDeltas = 1,
            Fault = new InvalidOperationException("the stream broke mid-flight"),
        };
        var kit = ChatTests.CreateKit(streaming: fake);
        kit.StreamPerSettings(true);
        kit.Localization
            .GetString("AI.Chat.Error", "the stream broke mid-flight")
            .Returns("The assistant is unavailable: the stream broke mid-flight");
        kit.Chat.AiChatInput = "How is the market?";

        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Chat.ChatMessages
            .Select(message => (message.Kind, message.Content))
            .Should().Equal(
                (ChatMessageKind.UserTurn, "How is the market?"),
                (ChatMessageKind.AssistantTurn, "alpha"),
                (ChatMessageKind.ErrorLine, "The assistant is unavailable: the stream broke mid-flight"));
        kit.Chat.IsBusy.Should().BeFalse("the faulted send unlocked the bus");
    }

    #endregion
}
