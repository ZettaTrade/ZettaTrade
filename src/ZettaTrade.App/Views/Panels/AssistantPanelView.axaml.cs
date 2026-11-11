namespace ZettaTrade.App.Views.Panels;

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The AI assistant panel (T-103): the extracted chat column the main
/// window's assistant column and the detached assistant window share. ONE
/// control instance exists — the detach moves it cross-window, so the
/// chat input keeps its identity and its focus affordance on either host.
/// The bubbles' copy affordance (T-158) lives HERE, view-side: the
/// view-model stays clipboard-free, the handler reads the pinned row off
/// the button's DataContext and hands its content to the top-level
/// clipboard best-effort.
/// </summary>
public partial class AssistantPanelView : UserControl
{
    /// <summary>
    /// Create a new AssistantPanelView instance.
    /// </summary>
    public AssistantPanelView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The copy pin's route (T-158): copy the pinned message's content to
    /// the clipboard. Best-effort by design — this is the app's first
    /// clipboard consumer, so a clipboard that refuses the write (a locked
    /// descriptor, a full session) stays a swallowed entry, never a dialog
    /// and never a broken chat.
    /// </summary>
    /// <param name="sender">The copy button that was pressed; its
    /// DataContext is the pinned message row.</param>
    /// <param name="e">The routed click event.</param>
    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { DataContext: ChatMessageViewModel message } &&
                TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(message.Content);
            }
        }
        catch (Exception)
        {
            // Best-effort by design (see the route's summary): the copy
            // silently does nothing on a clipboard fault.
        }
    }
}
