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
    public string AiChatInput { get; set; } = string.Empty;
    
    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    public void AddMessage(string role, string content)
    {
        ChatMessages.Add(new ChatMessageViewModel
        {
            Role = role,
            Content = content,
            Timestamp = System.DateTimeOffset.Now
        });
    }
}

/// <summary>
/// View model for a single chat message.
/// </summary>
public sealed class ChatMessageViewModel : ReactiveObject
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public System.DateTimeOffset Timestamp { get; init; } = System.DateTimeOffset.Now;
}
