namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using ReactiveUI;

/// <summary>
/// ViewModel for AI chat panel.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel only manages dynamic data (chat messages, user input).
/// </summary>
public sealed class ChatViewModel : ReactiveObject
{
    #region Properties

    /// <summary>User input text.</summary>
    public string AiChatInput { get; set; } = string.Empty;
    
    /// <summary>Collection of chat messages.</summary>
    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    #endregion Properties

    #region Public Methods

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
