namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for AI chat panel. Simple strings use direct dynamic data
/// binding; static localized labels live in XAML via {loc:Localized Key}.
/// The panel visibility is persisted state: the startup walk restores
/// <c>UI.ShowAiPanel</c> before any window opens (so the first frame shows
/// the user's layout with no flash), every user toggle persists
/// immediately, and the settings changes stream re-applies external writers
/// (the settings file hot-reload) live.
/// </summary>
public sealed class ChatViewModel : ReactiveObject, IAsyncInitializable
{
    /// <summary>
    /// The applied persisted state. The persistence pipeline only fires on a
    /// real difference against this cache, so both the startup restore and
    /// the changes-stream echo are absorbed without a settings rewrite.
    /// </summary>
    private bool _persistedShowAiPanel = UiSettingsKeys.DefaultShowAiPanel;

    private bool _isVisible = UiSettingsKeys.DefaultShowAiPanel;

    /// <summary>Create a new ChatViewModel instance.</summary>
    /// <param name="settings">The app settings service; the startup walk
    /// restores <see cref="UiSettingsKeys.ShowAiPanel"/> from it, every toggle
    /// persists there and the changes stream carries external writers.</param>
    /// <param name="logger">The class logger.</param>
    public ChatViewModel(
        ISettingsService settings,
        ILogger<ChatViewModel> logger)
    {
        _settings = settings;
        _logger = logger;

        // Persist every user flip immediately (the same real-difference
        // discipline the settings window applies): the cache is updated only
        // when the write lands, so the restore's assignment (cached BEFORE
        // the property is set) and the changes-stream echo never rewrite.
        this.WhenAnyValue(x => x.IsAiChatVisible)
            .Skip(1)
            .Where(value => value != _persistedShowAiPanel)
            .SelectMany(value => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ShowAiPanel,
                    value))
                .Select(_ => value))
            .Subscribe(
                issuedValue => _persistedShowAiPanel = issuedValue,
                ex => _logger.LogError(ex, "Failed to persist the AI panel visibility"));

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
    }

    private readonly ISettingsService _settings;
    private readonly ILogger<ChatViewModel> _logger;

    #region Properties

    /// <summary>User input text.</summary>
    public string AiChatInput { get; set; } = string.Empty;

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

    #endregion Properties

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

    /// <summary>
    /// Add a new message to the chat.
    /// </summary>
    public void AddMessage(string role, string content)
    {
        ChatMessages.Add(new ChatMessageViewModel
        {
            Role = role,
            Content = content,
            Timestamp = System.DateTimeOffset.Now
        });
    }

    #endregion Public Methods
}

/// <summary>
/// View model for a single chat message.
/// </summary>
public sealed class ChatMessageViewModel : ReactiveObject
{
    #region Properties

    /// <summary>Message role ("user" or "ai").</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public required string Content { get; init; }

    /// <summary>Message timestamp.</summary>
    public System.DateTimeOffset Timestamp { get; init; } = System.DateTimeOffset.Now;

    #endregion Properties
}
