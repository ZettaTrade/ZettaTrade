namespace ZettaTrade.App.ViewModels;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using Livery.Contracts;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.AI.Chat;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel for the AI chat panel. Static localized labels live in XAML via
/// <c>{loc:Localized Key}</c>; display labels of dynamic rows are resolved
/// through the localizer. The panel visibility is persisted state: the
/// startup walk restores <c>UI.ShowAiPanel</c> before any window opens (so
/// the first frame shows the user's layout with no flash), every user toggle
/// persists immediately, and the settings changes stream re-applies external
/// writers (the settings file hot-reload) live.
/// The send pipeline turns the input into a workspace-context completion:
/// the user turn is recorded first, the whole session history (sans error
/// lines) rides the request under a system prompt composed from the current
/// exchange, symbol, time frame and latest price, and the answer lands as an
/// assistant turn. When the user consented on the market-context chip, every
/// send additionally carries the token-bounded market snapshot preamble
/// (compacted fresh from the workspace moment) — un-consented sends keep the
/// exact pre-consent wording. The reply path streams when the provider
/// offers it and the settings key allows (T-205): the deltas append into
/// one assistant bubble while the answer is still being written, the Stop
/// affordance cuts the stream mid-flight (the arrived prefix stays), and
/// the non-streaming fallback rides the one-shot completion verbatim.
/// Unavailable assistant models surface honestly:
/// an in-chat error line with the underlying reason, never a silently
/// swallowed send.
/// The conversation's history of record persists (T-206): the picker lists
/// the stored conversations newest-first and replays the newest transcript
/// on launch, every send persists its turns (the user turn, the answer or
/// its arrived prefix, an honest error line), the first user turn
/// auto-titles the conversation, the picker swaps transcripts on demand,
/// and the export writes the transcript to Markdown. A storeless panel
/// (the tests' shape) runs memory-only — the pre-T-206 chat verbatim.
/// </summary>
public sealed class ChatViewModel : ReactiveObject, IAsyncInitializable
{
    #region Resx Keys

    /// <summary>In-chat failure line (the reason is appended by the UI).</summary>
    private const string ChatErrorKey = "AI.Chat.Error";

    /// <summary>Localized display labels of the chat row roles.</summary>
    private const string RoleUserKey = "AI.Chat.RoleUser";
    private const string RoleAssistantKey = "AI.Chat.RoleAssistant";

    /// <summary>The untitled conversation's display label (the picker and
    /// the export header fall back to it while no title was stored).</summary>
    internal const string ConversationDefaultTitleKey = "AI.Chat.ConversationDefaultTitle";

    /// <summary>In-chat failure line of a failed chat export.</summary>
    private const string ExportFailedKey = "AI.Chat.ExportFailed";

    /// <summary>The auto-title's length budget (characters before the ellipsis).</summary>
    private const int TitleLengthLimit = 48;

    /// <summary>The auto-title's overflow marker.</summary>
    private const string TitleEllipsis = "…";

    /// <summary>The streamed reply's re-render throttle, in milliseconds:
    /// the bubble's text re-renders at most this often while deltas stream
    /// (the settlement's final flush always lands). A fast token stream
    /// must not rebuild the bound text per delta — the transcript is a
    /// non-virtualized stack, so every flush re-measures the whole scroll
    /// content.</summary>
    private const int StreamRenderIntervalMillis = 50;

    #endregion

    #region Private Fields

    /// <summary>
    /// The applied persisted visibility state. The persistence pipeline only
    /// fires on a real difference against this cache, so both the startup
    /// restore and the changes-stream echo are absorbed without a settings
    /// rewrite.
    /// </summary>
    private bool _persistedShowAiPanel = UiSettingsKeys.DefaultShowAiPanel;

    private bool _isVisible = UiSettingsKeys.DefaultShowAiPanel;

    /// <summary>
    /// The applied persisted market-context consent. Mirrors the visibility
    /// discipline: the cache LEADS the persistence pipeline, so both the
    /// startup restore and the changes-stream echo are absorbed without a
    /// settings rewrite.
    /// </summary>
    private bool _persistedMarketContext = UiSettingsKeys.DefaultChatMarketContext;

    private bool _isMarketContextEnabled = UiSettingsKeys.DefaultChatMarketContext;
    private string _input = string.Empty;
    private bool _isBusy;

    /// <summary>
    /// The in-flight reply's cancellation source: the Stop command cancels
    /// it, the pipeline parks it while the request runs and clears it when
    /// the flow settles — a stop click after the reply settled reads null
    /// and stays a no-op.
    /// </summary>
    private CancellationTokenSource? _inFlight;

    /// <summary>
    /// Whether the reply in flight was stopped by a Stop click. The
    /// cancellation log reads it to tell a user stop apart from an
    /// abandonment (the source itself is already cleared by the time the
    /// cancellation unwinds, so the flag is the only witness that survives
    /// the settlement); StopReply sets it before the cancel, the flow
    /// clears it as it settles.
    /// </summary>
    private bool _userStopRequested;

    private readonly ISettingsService _settings;
    private readonly IChatCompletion _completion;
    private readonly ChatContextService _context;
    private readonly WorkspacePromptComposer _composer;
    private readonly MarketContextPreambleComposer? _momentComposer;

    /// <summary>
    /// The streaming half of the provider surface (T-205); without it the
    /// chat always rides the one-shot completion, whatever the settings key
    /// says — the key cannot turn a missing capability on.
    /// </summary>
    private readonly IStreamingChatCompletion? _streaming;

    /// <summary>
    /// The persistent history's store (T-206); without it the chat runs
    /// memory-only — no picker, no restore, no persistence, the pre-T-206
    /// shape verbatim.
    /// </summary>
    private readonly IChatHistoryStore? _history;

    /// <summary>
    /// The current conversation's identity — the authority the persistence
    /// and switching paths read. Null while no conversation is open (a
    /// storeless panel, or a fresh start whose first send creates the row).
    /// </summary>
    private Guid? _conversationId;

    /// <summary>The picker's applied selection (the OneWay binding reads it).</summary>
    private ChatConversationViewModel? _selectedConversation;

    private readonly ILocalizationManager _localization;
    private readonly ILogger<ChatViewModel> _logger;

    #endregion

    #region Constructor

    /// <summary>Create a new ChatViewModel instance.</summary>
    /// <param name="settings">The app settings service; the startup walk
    /// restores <see cref="UiSettingsKeys.ShowAiPanel"/> from it, every toggle
    /// persists there and the changes stream carries external writers.</param>
    /// <param name="completion">The one-shot chat completion provider
    /// (local models first).</param>
    /// <param name="context">The workspace snapshot service (exchange,
    /// symbol, time frame, latest price).</param>
    /// <param name="composer">The system prompt composer over the snapshot.</param>
    /// <param name="localization">The localization manager for dynamic row
    /// labels and the honest error line.</param>
    /// <param name="logger">The class logger.</param>
    /// <param name="momentComposer">The market-context preamble composer;
    /// without it the consent path degrades to no preamble (the consent
    /// chip still stores the preference, the send wording does not change).</param>
    /// <param name="streaming">The streaming half of the provider surface
    /// (T-205); without it every send rides the one-shot completion, whatever
    /// the settings key says.</param>
    /// <param name="chatHistory">The persistent history's store (T-206);
    /// without it the chat runs memory-only — no picker, no restore, no
    /// persistence.</param>
    public ChatViewModel(
        ISettingsService settings,
        IChatCompletion completion,
        ChatContextService context,
        WorkspacePromptComposer composer,
        ILocalizationManager localization,
        ILogger<ChatViewModel> logger,
        MarketContextPreambleComposer? momentComposer = null,
        IStreamingChatCompletion? streaming = null,
        IChatHistoryStore? chatHistory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _momentComposer = momentComposer;
        _streaming = streaming;
        _history = chatHistory;

        // Persist every user flip immediately (the same real-difference
        // discipline the settings window applies): the cache LEADS the
        // pipeline — it is updated at ISSUE time, in issuance order
        // (WhenAnyValue delivers on the calling thread, so property changes
        // order the pipeline) and the writes serialize behind it — while
        // the completion order of concurrent writes is out of our control.
        // The pre-fix completion-driven cache made a stale value visible to
        // the real-difference filter during an in-flight write: a rapid
        // toggle-pair whose writes completed out of order could leave the
        // second toggle dropped, so the settings file kept the older value
        // and the restart restored the old visibility (BUG-053).
        this.WhenAnyValue(x => x.IsAiChatVisible)
            .Skip(1)
            .Where(value => value != _persistedShowAiPanel)
            .Do(value => _persistedShowAiPanel = value)
            .Select(value => Observable
                .FromAsync(() => SetShowAiPanelAsync(value))
                .Select(_ => value))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The AI panel visibility pipeline stopped unexpectedly"));

        // External writers apply live: the cache leads the property change so
        // the persistence pipeline above sees an equal value and skips it.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ShowAiPanel)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                appliedValue =>
                {
                    _persistedShowAiPanel = appliedValue;
                    IsAiChatVisible = appliedValue;
                },
                ex => _logger.LogError(ex, "The AI panel visibility stream stopped unexpectedly"));

        // The market-context consent rides the same issued-order pipeline
        // (the BUG-053 discipline) on its own key: the chip's flip persists
        // immediately, external writers re-apply live.
        this.WhenAnyValue(x => x.IsMarketContextEnabled)
            .Skip(1)
            .Where(value => value != _persistedMarketContext)
            .Do(value => _persistedMarketContext = value)
            .Select(value => Observable
                .FromAsync(() => SetMarketContextAsync(value))
                .Select(_ => value))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The market-context consent pipeline stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChatMarketContext)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                appliedValue =>
                {
                    _persistedMarketContext = appliedValue;
                    IsMarketContextEnabled = appliedValue;
                },
                ex => _logger.LogError(ex, "The market-context consent stream stopped unexpectedly"));

        // The send button lives while there is text to send and no request is
        // in flight (one assistant request at a time; re-entrancy guard).
        SendTextCommand = ReactiveCommand.CreateFromTask(
            SendTextAsync,
            this.WhenAnyValue(
                x => x.AiChatInput,
                x => x.IsBusy,
                (input, busy) => !string.IsNullOrWhiteSpace(input) && !busy));

        // The Stop affordance (T-158) surfaces the engine's existing
        // cancellation seam: the in-flight completion already rides a
        // CancellationToken, so the command cancels its source. It offers
        // itself only while a reply runs — never a dead button — and the
        // guard keeps a raced click after the reply settled a no-op.
        StopCommand = ReactiveCommand.Create(
            StopReply,
            this.WhenAnyValue(x => x.IsBusy));

        // The consent chip flips the property; the persistence pipeline above
        // carries the write.
        ToggleContextCommand = ReactiveCommand.Create(() =>
        {
            IsMarketContextEnabled = !IsMarketContextEnabled;
        });

        // The fresh-conversation affordance (T-206): offered while no reply
        // runs (the busy guard keeps a raced click inert), it opens a new
        // stored conversation and clears the transcript.
        NewConversationCommand = ReactiveCommand.CreateFromTask(
            NewConversationAsync,
            this.WhenAnyValue(x => x.IsBusy).Select(busy => !busy));
    }

    #endregion

    #region Persistence

    /// <summary>
    /// One serialized persistence write for the AI panel visibility. A
    /// failed write is logged and swallowed HERE (not at the stream): the
    /// pipeline keeps serving later flips instead of dying on the first
    /// fault. The issued value stays cached after a failure — the file
    /// catches up with the property on the next real difference (the
    /// failure is visible in the log, the panel stays responsive with the
    /// trader's choice).
    /// </summary>
    /// <param name="visible">The issued visibility to persist.</param>
    /// <returns>The completed write.</returns>
    private async Task SetShowAiPanelAsync(bool visible)
    {
        try
        {
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ShowAiPanel,
                visible);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the AI panel visibility");
        }
    }

    /// <summary>
    /// One serialized persistence write for the market-context consent (the
    /// same failure model as the visibility write): a fault is logged and
    /// swallowed HERE, the pipeline keeps serving later flips, and the file
    /// catches up on the next real difference.
    /// </summary>
    /// <param name="enabled">The issued consent to persist.</param>
    /// <returns>The completed write.</returns>
    private async Task SetMarketContextAsync(bool enabled)
    {
        try
        {
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChatMarketContext,
                enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the market-context consent");
        }
    }

    #endregion

    #region Properties

    /// <summary>User input text; cleared when a send starts.</summary>
    public string AiChatInput
    {
        get => _input;
        set => this.RaiseAndSetIfChanged(ref _input, value);
    }

    /// <summary>Whether an assistant request is in flight (send locked).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>Collection of chat messages.</summary>
    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    /// <summary>
    /// The stored conversations the history picker lists (T-206), newest
    /// update first — the order the store answers in.
    /// </summary>
    public ObservableCollection<ChatConversationViewModel> Conversations { get; } = new();

    /// <summary>
    /// The picker's applied selection (T-206). The view binds it OneWay —
    /// the user's picks reach the VM through the picker's SelectionChanged
    /// route and land here after the transcript swap completes.
    /// </summary>
    public ChatConversationViewModel? SelectedConversation
    {
        get => _selectedConversation;
        private set => this.RaiseAndSetIfChanged(ref _selectedConversation, value);
    }

    /// <summary>
    /// Whether the assistant panel occupies the window's right column;
    /// toggled by the menu (hotkey <c>Ctrl+Shift+A</c>), mirrored by the menu
    /// item's checkable state.
    /// </summary>
    public bool IsAiChatVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    /// <summary>
    /// Whether every send carries the token-bounded market snapshot preamble
    /// (the market-context consent chip): the user's explicit choice, off
    /// until stored or toggled. The send pipeline reads it at issue time, so
    /// a flip between two sends changes exactly what it controls.
    /// </summary>
    public bool IsMarketContextEnabled
    {
        get => _isMarketContextEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isMarketContextEnabled, value);
    }

    #endregion

    #region Commands

    /// <summary>Send the typed message to the assistant (send button + Enter).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SendTextCommand { get; }

    /// <summary>
    /// Stop the in-flight assistant reply (T-158): cancels the completion's
    /// token, so the answer flow abandons and the input unlocks. Enabled
    /// only while a reply runs; a click after the reply settled is a no-op.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> StopCommand { get; }

    /// <summary>Flip the market-context consent chip (persisted pipeline rides
    /// the property change).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleContextCommand { get; }

    /// <summary>
    /// Open a fresh conversation (T-206): a new stored row (identity and all)
    /// enters the picker's top, becomes the selection and the transcript
    /// clears. Offered only while no reply runs.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> NewConversationCommand { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Toggle the assistant panel column, the semantics the menu hotkey
    /// carries: visible → hidden, hidden → visible.
    /// </summary>
    /// <returns>The visibility after the toggle: <c>true</c> when the panel
    /// was (just) shown, so the caller can ask the View to focus the input.</returns>
    public async Task<bool> ToggleAsync()
    {
        IsAiChatVisible = !IsAiChatVisible;

        return IsAiChatVisible;
    }

    /// <summary>
    /// Route a composed trade review into the chat (T-255): the panel shows
    /// (a hidden one toggles visible) and the prompt rides the standard
    /// send discipline — the user turn lands, persists, and the provider
    /// answers through the very pipeline a typed message takes (the
    /// localized failure line, the Stop affordance, the busy guard
    /// included). While a reply is in flight the prompt parks in the
    /// input row: the user sends it when the answer settles — one
    /// assistant request at a time, never a queue jump. Strictly
    /// read-only toward everything else: no trading decision, no
    /// auto-posting — the review is a user turn, nothing more.
    /// </summary>
    public async Task SubmitReviewAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        if (!IsAiChatVisible)
        {
            await ToggleAsync();
        }

        if (IsBusy)
        {
            AiChatInput = prompt;
            return;
        }

        AiChatInput = prompt;
        await SendTextAsync(CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var restored = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            UiSettingsKeys.DefaultShowAiPanel);

        // The cache leads the assignment on purpose: a flip the persistence
        // pipeline would observe is filtered out by the real-difference
        // guard, so a restore never rewrites the value it just read.
        _persistedShowAiPanel = restored;
        IsAiChatVisible = restored;

        var marketContextRestored = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChatMarketContext,
            UiSettingsKeys.DefaultChatMarketContext);
        _persistedMarketContext = marketContextRestored;
        IsMarketContextEnabled = marketContextRestored;

        _logger.LogInformation(
            "Restored persisted AI panel visibility: '{Visibility}'",
            restored ? "visible" : "hidden");
        _logger.LogInformation(
            "Restored persisted market-context consent: '{Consent}'",
            marketContextRestored ? "on" : "off");

        // The persistent history (T-206) restores last: the picker fills
        // from the store (newest first) and the newest conversation's
        // transcript lands in the chat.
        await RestoreHistoryAsync(ct);
    }

    #endregion

    #region Send Pipeline

    private async Task SendTextAsync(CancellationToken ct)
    {
        var input = AiChatInput;
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        AddTurn(ChatMessageKind.UserTurn, input, RoleUserKey);
        // The send is accepted: the input row yields to the answer (cleared
        // BEFORE the persistence write — the empty input keeps the send
        // command's guard closed against a re-entry while the write runs).
        AiChatInput = string.Empty;
        await PersistUserTurnAsync(input);

        IsBusy = true;
        try
        {
            // The reply rides a source the Stop command can reach: linked to
            // the command's own token, so the host's abandonment (shutdown)
            // still cancels everything — the stop adds one more cancel path,
            // never a competing one.
            using var inFlight = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _inFlight = inFlight;
            try
            {
                var turns = ChatMessages
                    .Where(message => message.Kind != ChatMessageKind.ErrorLine)
                    .Select(message => message.Kind == ChatMessageKind.UserTurn
                        ? new ChatTurn(ChatTurn.UserRole, message.Content)
                        : new ChatTurn(ChatTurn.AssistantRole, message.Content))
                    .ToList();

                var workspace = _context.Current;
                var systemPrompt = IsMarketContextEnabled && _momentComposer is not null
                    ? _composer.Compose(
                        workspace,
                        _momentComposer.Compose(workspace.ToMarketMoment()))
                    : _composer.Compose(workspace);

                // The path is decided at issue time (the provider's own
                // per-request settings discipline): a settings-file flip
                // between sends changes exactly what the next send does.
                // Without the streaming capability the key cannot turn the
                // streamed path on — the one-shot completion is the standing
                // shape.
                var stream = _streaming is not null && await _settings.GetAsync<bool>(
                    AiSettingsKeys.Section,
                    AiSettingsKeys.Streaming,
                    AiSettingsKeys.DefaultStreaming);

                if (stream)
                {
                    await StreamReplyAsync(systemPrompt, turns, inFlight.Token);
                }
                else
                {
                    var reply = await _completion.CompleteAsync(systemPrompt, turns, inFlight.Token);

                    AddTurn(ChatMessageKind.AssistantTurn, reply, RoleAssistantKey);
                    await PersistTurnAsync(ChatHistoryMessage.AssistantKind, reply);
                }
            }
            finally
            {
                // Cleared BEFORE the busy flag drops: the Stop command's
                // disablement and the no-op guard cross-fade here — a stop
                // click racing the settlement reads null and stays a no-op
                // (the source itself is never disposed while a click may
                // still reach it, so Cancel cannot object).
                _inFlight = null;
            }
        }
        catch (OperationCanceledException)
        {
            // The request was abandoned (the user stopped it — T-158 — or the
            // shutdown/hot-reload path canceled it): the accepted turn stays
            // in the chat, no answer and no error line follows. A streamed
            // reply's arrived prefix (T-205) stays as the partial turn — the
            // deltas are the model's real output, and the honest shape of a
            // cut stream is "everything that made it". The stop
            // flag tells the two apart — the source is already cleared by
            // the time the cancellation unwinds, so the flag is the only
            // witness that survived the settlement; the click's own
            // Information entry rode StopReply, a cancellation without one
            // is an abandonment.
            if (_userStopRequested)
            {
                _logger.LogInformation("The assistant reply was stopped by the user");
            }
            else
            {
                _logger.LogWarning("The assistant request was abandoned before it answered");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The assistant request failed");
            var line = _localization.GetString(ChatErrorKey, ex.Message);
            AddTurn(ChatMessageKind.ErrorLine, line, RoleAssistantKey);
            await PersistTurnAsync(ChatHistoryMessage.ErrorKind, line);
        }
        finally
        {
            _userStopRequested = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// The Stop command's route: cancel the in-flight source so the
    /// completion abandons. A raced click after the reply settled reads no
    /// source and stays a no-op (the command is disabled by then anyway).
    /// </summary>
    private void StopReply()
    {
        if (_inFlight is null)
        {
            return;
        }

        // The flag goes up before the cancel: the cancellation unwinds
        // through the pipeline while the flag is the surviving witness of
        // WHO canceled — a user stop logs Information, an abandonment
        // (shutdown, the provider's own token) logs Warning.
        _userStopRequested = true;
        _logger.LogInformation("The user pressed Stop on the in-flight assistant reply");
        _inFlight.Cancel();
    }

    #endregion

    #region Streamed Reply

    /// <summary>
    /// The streamed reply (T-205): the deltas append into ONE assistant
    /// bubble created on the first delta — an immediately-failing stream
    /// never renders an empty bubble — and the bubble grows while the answer
    /// is still being written. The re-render throttles to a bounded cadence
    /// while the deltas append into the buffer every time: a fast token
    /// stream must not rebuild the bound text per token (the transcript
    /// re-measures per flush), and the settlement's final flush lands the
    /// tail the throttle held back. The reader's contract throws on a
    /// stream that closes without any content, so a normal completion always
    /// carries the full answer in the bubble (no second append happens).
    /// A mid-flight fault keeps the arrived prefix in the bubble (the
    /// deltas are the model's real output); the caller's catch renders the
    /// error line after it — except a stop, which keeps the prefix with no
    /// error line (the T-158 semantics, the streamed prefix included).
    /// The arrived content persists whatever the settlement (T-206): the
    /// finally rides the completed answer, the faulted prefix and the
    /// stopped prefix alike.
    /// </summary>
    /// <param name="systemPrompt">The composed workspace prompt.</param>
    /// <param name="turns">The conversation history for the request.</param>
    /// <param name="ct">The in-flight token (the Stop affordance cancels it).</param>
    /// <returns>The completed stream (the bubble holds the full answer).</returns>
    private async Task StreamReplyAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        CancellationToken ct)
    {
        var content = new StringBuilder();
        ChatMessageViewModel? bubble = null;
        var lastRender = 0L;

        // Render one arrived state into the transcript. The bubble is born
        // on the first delta (an immediately-failing stream never renders
        // an empty bubble); after that the re-render throttles to the
        // interval: a fast token stream must not rebuild the bound text per
        // delta — every flush rebuilds the whole answer string and the
        // non-virtualized transcript re-measures, so an unthrottled stream
        // stutters the UI on long answers. The settlement's final flush
        // lands the tail the throttle held back.
        void RenderArrived(bool force)
        {
            if (!force && Environment.TickCount64 - lastRender < StreamRenderIntervalMillis)
            {
                return;
            }

            lastRender = Environment.TickCount64;
            if (bubble is null)
            {
                bubble = new ChatMessageViewModel
                {
                    Kind = ChatMessageKind.AssistantTurn,
                    Role = _localization.GetString(RoleAssistantKey),
                    Content = content.ToString(),
                };
                ChatMessages.Add(bubble);
            }
            else
            {
                bubble.Content = content.ToString();
            }
        }

        try
        {
            await foreach (var delta in _streaming!.StreamAsync(systemPrompt, turns, ct))
            {
                content.Append(delta);
                RenderArrived(force: bubble is null);
            }
        }
        finally
        {
            // The final flush first: the settled bubble — and the persisted
            // content it carries — must hold everything that arrived, the
            // throttle's tail included.
            //
            // The arrived content persists whatever the settlement (T-206):
            // a completed answer, a mid-flight fault's prefix, a Stop cut's
            // prefix — the deltas are the model's real output. An
            // immediately-failing stream never rendered a bubble, so nothing
            // lands; the error line, when one follows, persists separately
            // in the caller's catch. The write rides no in-flight token — a
            // stopped reply must still record its prefix.
            if (bubble is not null)
            {
                bubble.Content = content.ToString();
                await PersistTurnAsync(ChatHistoryMessage.AssistantKind, bubble.Content);
            }
        }
    }

    #endregion

    #region Persistent History

    /// <summary>
    /// The picker's fill and the newest conversation's replay (T-206). A
    /// faulted read leaves the panel standing — an empty picker and an empty
    /// chat, logged and swallowed (the history is a convenience, never the
    /// trading flow); the next send starts a fresh conversation, and the
    /// picker's rows re-read on the next launch.
    /// </summary>
    /// <param name="ct">The startup walk's token.</param>
    /// <returns>The completed restore.</returns>
    private async Task RestoreHistoryAsync(CancellationToken ct)
    {
        var history = _history;
        if (history is null)
        {
            return;
        }

        IReadOnlyList<ChatConversation> conversations;
        try
        {
            conversations = await history.ListConversationsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the chat history picker");
            return;
        }

        foreach (var conversation in conversations)
        {
            Conversations.Add(new ChatConversationViewModel(_localization, conversation));
        }

        if (Conversations.Count > 0)
        {
            await SelectConversationAsync(Conversations[0]);
        }
    }

    /// <summary>
    /// Swap the transcript to the picked conversation (the picker's
    /// SelectionChanged route, T-206). The guards: a storeless panel never
    /// switches, a null pick is nothing to switch to, a busy reply owns the
    /// transcript until it settles, and the conversation already on display
    /// (the identity reads against <see cref="_conversationId"/>) no-ops —
    /// that last guard also absorbs the OneWay binding's re-entrant echo.
    /// A faulted load stays put: the current transcript keeps serving, the
    /// failure is logged, the pick retries honestly on the next attempt.
    /// </summary>
    /// <param name="conversation">The picked conversation row.</param>
    /// <returns>The completed swap.</returns>
    public async Task SelectConversationAsync(ChatConversationViewModel? conversation)
    {
        var history = _history;
        if (history is null ||
            conversation is null ||
            IsBusy ||
            conversation.Id == _conversationId)
        {
            return;
        }

        IReadOnlyList<ChatHistoryMessage> transcript;
        try
        {
            transcript = await history.LoadMessagesAsync(conversation.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load the chat conversation {ConversationId}",
                conversation.Id);
            return;
        }

        // The identity field leads the property assignment: a re-entrant
        // picker event (the OneWay binding pushing the selection back) sees
        // the id already set and Id-guards out before a second load could
        // race the rebuild.
        _conversationId = conversation.Id;
        SelectedConversation = conversation;

        ChatMessages.Clear();
        foreach (var message in transcript)
        {
            if (KindFromStored(message.Kind) is not { } kind)
            {
                _logger.LogWarning(
                    "Chat message dropped on restore: unknown kind name {Kind}",
                    message.Kind);
                continue;
            }

            ChatMessages.Add(new ChatMessageViewModel
            {
                Kind = kind,
                Role = _localization.GetString(
                    kind == ChatMessageKind.UserTurn ? RoleUserKey : RoleAssistantKey),
                Content = message.Content,
                Timestamp = message.TimestampUtc.ToLocalTime(),
            });
        }
    }

    /// <summary>
    /// The fresh-conversation affordance's route (T-206): a new stored row
    /// enters the picker's top, becomes the selection, the transcript
    /// clears. The identity field leads the assignment (the same re-entrancy
    /// discipline the switch applies). A faulted create keeps the current
    /// conversation on display — the trader's click retries honestly. The
    /// storeless panel degrades to a memory-only fresh chat.
    /// </summary>
    /// <param name="ct">The command's token.</param>
    /// <returns>The completed swap.</returns>
    private async Task NewConversationAsync(CancellationToken ct)
    {
        var history = _history;
        if (history is null)
        {
            ChatMessages.Clear();
            return;
        }

        try
        {
            var created = await history.CreateConversationAsync(ct);
            var conversation = new ChatConversationViewModel(_localization, created);
            Conversations.Insert(0, conversation);

            _conversationId = conversation.Id;
            SelectedConversation = conversation;
            ChatMessages.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a new chat conversation");
        }
    }

    /// <summary>The stored kind name back into a row kind; an unknown name drops.</summary>
    private static ChatMessageKind? KindFromStored(string kind) => kind switch
    {
        ChatHistoryMessage.UserKind => ChatMessageKind.UserTurn,
        ChatHistoryMessage.AssistantKind => ChatMessageKind.AssistantTurn,
        ChatHistoryMessage.ErrorKind => ChatMessageKind.ErrorLine,
        _ => null,
    };

    /// <summary>
    /// Persist the user's turn (T-206): the first send opens the
    /// conversation (the lazily-created row enters the picker and becomes
    /// the selection), every send appends and — on the conversation's first
    /// titled turn — auto-titles it from the input. All faults log and
    /// swallow: the chat never breaks on its history.
    /// </summary>
    /// <param name="content">The user turn's text.</param>
    /// <returns>The completed write.</returns>
    private async Task PersistUserTurnAsync(string content)
    {
        var history = _history;
        if (history is null)
        {
            return;
        }

        try
        {
            var conversation = await EnsureConversationAsync();
            if (conversation is null)
            {
                return;
            }

            await history.AppendMessageAsync(
                conversation.Id,
                new ChatHistoryMessage(ChatHistoryMessage.UserKind, content, DateTimeOffset.UtcNow));

            await AutoTitleAsync(conversation, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chat user turn");
        }
    }

    /// <summary>
    /// Persist an already-rendered turn (the one-shot reply, the streamed
    /// settlement, the error line — T-206). The turn rides the conversation
    /// already on record: a send whose conversation could not be opened (the
    /// user turn's create faulted) drops its reply too — the record never
    /// carries an answer without its question. Faults log and swallow.
    /// </summary>
    /// <param name="kind">The stored kind name.</param>
    /// <param name="content">The turn's text.</param>
    /// <returns>The completed write.</returns>
    private async Task PersistTurnAsync(string kind, string content)
    {
        var history = _history;
        if (history is null || _conversationId is not { } conversationId)
        {
            return;
        }

        try
        {
            await history.AppendMessageAsync(
                conversationId,
                new ChatHistoryMessage(kind, content, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chat turn ({Kind})", kind);
        }
    }

    /// <summary>
    /// The current send's conversation, opened on demand (T-206): the
    /// standing identity returns its picker row; a fresh start creates the
    /// row (untitled — the auto-title writes the first turn's), inserts it
    /// at the picker's top and makes it the selection. A faulted create
    /// answers null — the caller drops the write (already logged upstream
    /// by this method's caller catch).
    /// </summary>
    /// <returns>The conversation row, or null when the create faulted.</returns>
    private async Task<ChatConversationViewModel?> EnsureConversationAsync()
    {
        if (_conversationId is { } id)
        {
            return Conversations.FirstOrDefault(conversation => conversation.Id == id);
        }

        var created = await _history!.CreateConversationAsync();
        var conversation = new ChatConversationViewModel(_localization, created);
        Conversations.Insert(0, conversation);

        // The identity field leads the property assignment (the re-entrancy
        // discipline the switch applies).
        _conversationId = conversation.Id;
        SelectedConversation = conversation;
        return conversation;
    }

    /// <summary>
    /// The conversation's auto-title (T-206): the FIRST titled turn's input
    /// becomes the title (trimmed, at most 48 characters plus the ellipsis)
    /// through a rename, and the stored-title flag keeps it a once-per-
    /// conversation affair — later turns never retitle.
    /// </summary>
    /// <param name="conversation">The conversation row to title.</param>
    /// <param name="content">The user turn's text.</param>
    /// <returns>The completed rename.</returns>
    private async Task AutoTitleAsync(ChatConversationViewModel conversation, string content)
    {
        if (conversation.HasStoredTitle)
        {
            return;
        }

        var title = AutoTitleFrom(content);
        if (title is null)
        {
            return;
        }

        await _history!.RenameConversationAsync(conversation.Id, title);
        conversation.ApplyTitle(title);
    }

    /// <summary>The auto-title text for an input: trimmed, 48 characters at most.</summary>
    private static string? AutoTitleFrom(string input)
    {
        var trimmed = input.Trim();
        return trimmed.Length == 0
            ? null
            : trimmed.Length > TitleLengthLimit
                ? trimmed[..TitleLengthLimit] + TitleEllipsis
                : trimmed;
    }

    #endregion

    #region Export

    /// <summary>
    /// The transcript's Markdown export (T-206): a title line, then one
    /// labelled paragraph per row — the exact text a fresh read renders,
    /// byte for byte deterministic (the invariant culture formats the
    /// moment).
    /// </summary>
    /// <returns>The export document.</returns>
    public string BuildExportMarkdown()
    {
        var title = SelectedConversation?.DisplayTitle
            ?? _localization.GetString(ConversationDefaultTitleKey);
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\n\n");
        foreach (var message in ChatMessages)
        {
            builder.Append("**").Append(message.Role).Append("** (")
                .Append(message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(")\n\n")
                .Append(message.Content).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>The export's suggested file name: the chat stamp of the moment.</summary>
    /// <returns>The pre-filled file name (no directory part).</returns>
    public string BuildExportFileName() =>
        $"zettatrade-chat-{DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md";

    /// <summary>
    /// Write the export document to the picked path (T-206), UTF-8 with no
    /// byte-order mark. A failed write is logged, renders an in-chat error
    /// line (the honest surface of a dead export) and answers false — the
    /// chat itself never breaks on it.
    /// </summary>
    /// <param name="path">The absolute destination path.</param>
    /// <returns>Whether the write succeeded.</returns>
    public async Task<bool> ExportToAsync(string path)
    {
        try
        {
            await File.WriteAllTextAsync(path, BuildExportMarkdown(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The chat export to {Path} failed", path);
            AddTurn(
                ChatMessageKind.ErrorLine,
                _localization.GetString(ExportFailedKey, ex.Message),
                RoleAssistantKey);
            return false;
        }
    }

    #endregion

    #region Messages

    /// <summary>Append one row with its role-specific display label.</summary>
    private void AddTurn(ChatMessageKind kind, string content, string roleKey)
    {
        ChatMessages.Add(new ChatMessageViewModel
        {
            Kind = kind,
            Role = _localization.GetString(roleKey),
            Content = content,
        });
    }

    #endregion
}

/// <summary>Which pipeline produced a chat row (drives the request history).</summary>
public enum ChatMessageKind
{
    /// <summary>The trader's turn (rides every request).</summary>
    UserTurn,

    /// <summary>The assistant's answer (rides later requests as context).</summary>
    AssistantTurn,

    /// <summary>An honest failure line (rendered, never sent to the model).</summary>
    ErrorLine,
}

/// <summary>
/// View model for a single chat message.
/// </summary>
public sealed class ChatMessageViewModel : ReactiveObject
{
    #region Properties

    /// <summary>Which pipeline produced this row.</summary>
    public ChatMessageKind Kind { get; init; } = ChatMessageKind.UserTurn;

    /// <summary>Whether this row is the trader's turn (the tinted bubble,
    /// T-158: the position and the tint carry the authorship).</summary>
    public bool IsUserTurn => Kind == ChatMessageKind.UserTurn;

    /// <summary>Whether this row is the assistant's answer (the raised
    /// Surface bubble, T-158).</summary>
    public bool IsAssistantTurn => Kind == ChatMessageKind.AssistantTurn;

    /// <summary>Whether this row is an in-chat failure line (the
    /// transparent error variant, T-158).</summary>
    public bool IsErrorLine => Kind == ChatMessageKind.ErrorLine;

    /// <summary>Localized display label of the row's role.</summary>
    public required string Role { get; init; }

    private string _content = string.Empty;

    /// <summary>
    /// Message content. The streamed reply's bubble mutates it live while
    /// the answer is still being written (T-205): every append raises the
    /// change, so the bound text re-renders with the arrived prefix.
    /// </summary>
    public required string Content
    {
        get => _content;
        set => this.RaiseAndSetIfChanged(ref _content, value);
    }

    /// <summary>Message timestamp.</summary>
    public System.DateTimeOffset Timestamp { get; init; } = System.DateTimeOffset.Now;

    #endregion
}

/// <summary>
/// View model for one conversation row in the history picker (T-206): the
/// stored identity plus the display title — the first user turn's
/// auto-title, or the localized default while the conversation carries no
/// stored title yet.
/// </summary>
public sealed class ChatConversationViewModel : ReactiveObject
{
    #region Private Fields

    private readonly ILocalizationManager _localization;

    private string? _title;

    /// <summary>Whether a title was already stored (the auto-title fires once).</summary>
    private bool _hasStoredTitle;

    #endregion

    #region Constructor

    /// <summary>Create the picker row over its stored record.</summary>
    /// <param name="localization">The localization manager for the default label.</param>
    /// <param name="conversation">The stored record the row renders.</param>
    public ChatConversationViewModel(ILocalizationManager localization, ChatConversation conversation)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Id = conversation.Id;
        _title = conversation.Title;
        _hasStoredTitle = conversation.Title is not null;
    }

    #endregion

    #region Properties

    /// <summary>The stored conversation identity.</summary>
    public Guid Id { get; }

    /// <summary>The stored title, or <c>null</c> while the conversation is untitled.</summary>
    public string? Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    /// <summary>Whether a title was already stored (the auto-title fires once).</summary>
    public bool HasStoredTitle
    {
        get => _hasStoredTitle;
        private set => this.RaiseAndSetIfChanged(ref _hasStoredTitle, value);
    }

    /// <summary>The picker's display label: the stored title or the localized default.</summary>
    public string DisplayTitle => _title ?? _localization.GetString(ChatViewModel.ConversationDefaultTitleKey);

    #endregion

    #region Public Methods

    /// <summary>
    /// Apply a stored title (the auto-title's rename): the display label
    /// re-renders and the stored-title flag rides along, so the auto-title
    /// stays a once-per-conversation affair.
    /// </summary>
    /// <param name="title">The stored title.</param>
    public void ApplyTitle(string title)
    {
        Title = title;
        HasStoredTitle = true;
        this.RaisePropertyChanged(nameof(DisplayTitle));
    }

    #endregion
}

