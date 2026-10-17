namespace ZettaTrade.App.ViewModels;

using System.Reactive;
using ReactiveUI;

/// <summary>
/// ViewModel for menu bar with commands.
/// Localization is handled directly in XAML via {loc:Localized Key}.
/// </summary>
public sealed class MenuViewModel : ReactiveObject
{
    public ReactiveCommand<Unit, Unit> NewOrderCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPositionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowOrderHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAiChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ManageMcpServersCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }

    public MenuViewModel()
    {
        // Initialize commands with explicit Unit type
        NewOrderCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleNewOrderAsync();
            return Unit.Default;
        });
        
        ShowPositionsCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleShowPositionsAsync();
            return Unit.Default;
        });
        
        ShowOrderHistoryCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleShowOrderHistoryAsync();
            return Unit.Default;
        });
        
        OpenAiChatCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleOpenAiChatAsync();
            return Unit.Default;
        });
        
        ManageMcpServersCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleManageMcpServersAsync();
            return Unit.Default;
        });
        
        OpenSettingsCommand = ReactiveCommand.CreateFromTask<Unit, Unit>(async _ =>
        {
            await HandleOpenSettingsAsync();
            return Unit.Default;
        });
        
        ExitCommand = ReactiveCommand.Create<Unit>(() =>
        {
            HandleExit();
            return Unit.Default;
        });
    }

    private Task HandleNewOrderAsync()
    {
        // TODO: Implement new order dialog
        return Task.CompletedTask;
    }

    private Task HandleShowPositionsAsync()
    {
        // TODO: Implement positions view
        return Task.CompletedTask;
    }

    private Task HandleShowOrderHistoryAsync()
    {
        // TODO: Implement order history view
        return Task.CompletedTask;
    }

    private Task HandleOpenAiChatAsync()
    {
        // TODO: Implement AI chat
        return Task.CompletedTask;
    }

    private Task HandleManageMcpServersAsync()
    {
        // TODO: Implement MCP servers management
        return Task.CompletedTask;
    }

    private Task HandleOpenSettingsAsync()
    {
        // TODO: Implement settings dialog
        return Task.CompletedTask;
    }

    private void HandleExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }
}
