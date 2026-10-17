using System.Collections.ObjectModel;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

namespace ZettaTrade.App.ViewModels;

/// <summary>
/// ViewModel for AI chat panel.
/// </summary>
public sealed class ChatViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    private readonly ILocalizationManager _localization;

    [Reactive] public string AiChatInput { get; set; } = string.Empty;
    [Reactive] public string AssistantTitle { get; set; } = string.Empty;
    [Reactive] public string ChatWatermark { get; set; } = string.Empty;
    
    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    public ChatViewModel(ILocalizationManager localization)
    {
        _localization = localization;

        this.WhenActivated(disposables =>
        {
            // Subscribe to language changes
            _localization.LanguageChanged
                .Subscribe(_ => UpdateLocalizedStrings())
                .DisposeWith(disposables);

            UpdateLocalizedStrings();
        });
    }

    private void UpdateLocalizedStrings()
    {
        AssistantTitle = _localization.GetString("AI.Assistant");
        ChatWatermark = _localization.GetString("AI.Watermark");
    }

    public void AddMessage(string role, string content)
    {
        ChatMessages.Add(new ChatMessageViewModel
        {
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.Now
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
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
