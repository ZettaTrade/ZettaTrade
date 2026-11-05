namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
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
/// assistant turn. Unavailable assistant models surface honestly: an in-chat
/// error line with the underlying reason, never a silently swallowed send.
/// </summary>
public sealed class ChatViewModel : ReactiveObject, IAsyncInitializable
{
    #region Resx Keys

    /// <summary>In-chat failure line (the reason is appended by the UI).</summary>
    private const string ChatErrorKey = "AI.Chat.Error";

    /// <summary>Display labels of the chat row roles.</summary>
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
    private string _input = string.Empty;
    private bool _isBusy;

    private readonly ISettingsService _settings;
    private readonly IChatCompletion _completion;
    private readonly ChatContextService _context;
    private readonly WorkspacePromptComposer _composer;
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
    public ChatViewModel(
        ISettingsService settings,
        IChatCompletion completion,
        ChatContextService context,
        WorkspacePromptComposer composer,
        ILocalizationManager localization,
        ILogger<ChatViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

        // The send button lives while there is text to send and no request is
        // in flight (one assistant request at a time; re-entrancy guard).
        SendTextCommand = ReactiveCommand.CreateFromTask(
            SendTextAsync,
            this.WhenAnyValue(
                x => x.AiChatInput,
                x => x.IsBusy,
                (input, busy) => !string.IsNullOrWhiteSpace(input) && !busy));
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

    #endregion

    #region Commands

    /// <summary>Send the typed message to the assistant (send button + Enter).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SendTextCommand { get; }

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

        _logger.LogInformation(
            "Restored persisted AI panel visibility: '{Visibility}'",
            restored ? "visible" : "hidden");
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
            var turns = ChatMessages
                .Where(message => message.Kind != ChatMessageKind.ErrorLine)
                .Select(message => message.Kind == ChatMessageKind.UserTurn
                    ? new ChatTurn(ChatTurn.UserRole, message.Content)
                    : new ChatTurn(ChatTurn.AssistantRole, message.Content))
                .ToList();

            var reply = await _completion.CompleteAsync(
                _composer.Compose(_context.Current),
                turns,
                ct);

            AddTurn(ChatMessageKind.AssistantTurn, reply, RoleAssistantKey);
        }
        catch (OperationCanceledException)
        {
            // The request was abandoned (shutdown/hot-reload path): the
            // accepted turn stays in the chat, no answer follows.
            _logger.LogWarning("The assistant request was abandoned before it answered");
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
            IsBusy = false;
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

    /// <summary>Localized display label of the row's role.</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public required string Content { get; init; }

    /// <summary>Message timestamp.</summary>
    public System.DateTimeOffset Timestamp { get; init; } = System.DateTimeOffset.Now;

    #endregion
}
