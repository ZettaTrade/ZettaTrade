namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Text;
using Livery.Contracts;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.AI.Chat;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
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
/// </summary>
public sealed class ChatViewModel : ReactiveObject, IAsyncInitializable
{
    #region Resx Keys

    /// <summary>In-chat failure line (the reason is appended by the UI).</summary>
    private const string ChatErrorKey = "AI.Chat.Error";

    /// <summary>Localized display labels of the chat row roles.</summary>
    private const string RoleUserKey = "AI.Chat.RoleUser";
    private const string RoleAssistantKey = "AI.Chat.RoleAssistant";

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
    public ChatViewModel(
        ISettingsService settings,
        IChatCompletion completion,
        ChatContextService context,
        WorkspacePromptComposer composer,
        ILocalizationManager localization,
        ILogger<ChatViewModel> logger,
        MarketContextPreambleComposer? momentComposer = null,
        IStreamingChatCompletion? streaming = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _momentComposer = momentComposer;
        _streaming = streaming;

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
        // The send is accepted: the input row yields to the answer.
        AiChatInput = string.Empty;

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
            AddTurn(
                ChatMessageKind.ErrorLine,
                _localization.GetString(ChatErrorKey, ex.Message),
                RoleAssistantKey);
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
    /// is still being written. Every append raises the content change, so
    /// the bound text re-renders live. The reader's contract throws on a
    /// stream that closes without any content, so a normal completion always
    /// carries the full answer in the bubble (no second append happens).
    /// A mid-flight fault keeps the arrived prefix in the bubble (the
    /// deltas are the model's real output); the caller's catch renders the
    /// error line after it — except a stop, which keeps the prefix with no
    /// error line (the T-158 semantics, the streamed prefix included).
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

        await foreach (var delta in _streaming!.StreamAsync(systemPrompt, turns, ct))
        {
            content.Append(delta);
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

