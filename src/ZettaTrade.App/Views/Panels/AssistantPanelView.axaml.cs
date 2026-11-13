namespace ZettaTrade.App.Views.Panels;

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;

/// <summary>
/// The AI assistant panel (T-103): the extracted chat column the main
/// window's assistant column and the detached assistant window share. ONE
/// control instance exists — the detach moves it cross-window, so the
/// chat input keeps its identity and its focus affordance on either host.
/// The bubbles' copy affordance (T-158) lives HERE, view-side: the
/// view-model stays clipboard-free, the handler reads the pinned row off
/// the button's DataContext and hands its content to the top-level
/// clipboard best-effort.
/// The history surface (T-206) rides the same shape: the picker's
/// SelectionChanged hands the picked conversation to the view model's
/// awaited switch, and the export button walks the save-file picker to the
/// awaited export — the one control instance serves both hosts.
/// </summary>
public partial class AssistantPanelView : UserControl
{
    /// <summary>
    /// Create the AI assistant panel around its view model (T-175): the
    /// DataContext is a LocalValue from the panel's own construction, so the
    /// subtree's compiled bindings evaluate against the chat VM from their
    /// very first pass. The XAML-created alternative let the loader wire the
    /// inherited MainViewModel into the unattached panel ahead of every
    /// tree link, cast-erroring the whole subtree on startup (the noise the
    /// T-175 sweep kills); the parameterless route keeps the designer
    /// preview working and the T-175 counterfactual reproducible.
    /// </summary>
    public AssistantPanelView(ChatViewModel? chat)
    {
        DataContext = chat;

        InitializeComponent();
    }

    /// <summary>
    /// The bare construction (the designer's route): the caller assigns the
    /// DataContext later; the startup cast-noise shape lives here.
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

    /// <summary>
    /// The conversation picker's route (T-206): hand the picked conversation
    /// to the view model's awaited switch — the deterministic await keeps
    /// the swap observable (never a fire-and-forget racing the transcript
    /// rebuild). The VM's own guards (storeless, null pick, busy, the
    /// conversation already on display) absorb everything this handler
    /// cannot know.
    /// </summary>
    /// <param name="sender">The picker whose selection changed.</param>
    /// <param name="e">The selection change (the added item is the pick).</param>
    private async void OnConversationSelected(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DataContext is ChatViewModel chat &&
                e.AddedItems.Count > 0 &&
                e.AddedItems[0] is ChatConversationViewModel conversation)
            {
                await chat.SelectConversationAsync(conversation);
            }
        }
        catch (Exception)
        {
            // The switch faults log and swallow inside the VM; this net only
            // keeps the event route itself from surfacing — the chat never
            // breaks on its history.
        }
    }

    /// <summary>
    /// The export button's route (T-206): the save-file picker pre-fills the
    /// chat-stamped name (a cancelled pick exports nothing), then the
    /// awaited export writes the transcript. Best-effort by design — a
    /// picker fault stays a swallowed entry, the chat never breaks on it
    /// (a failed write renders its own in-chat error line inside the VM).
    /// </summary>
    /// <param name="sender">The export button that was pressed.</param>
    /// <param name="e">The routed click event.</param>
    private async void OnExportChatClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is ChatViewModel chat &&
                TopLevel.GetTopLevel(this) is Window owner)
            {
                var path = await SaveFilePicker.PickAsync(
                    owner,
                    chat.BuildExportFileName(),
                    "md",
                    "Markdown");
                if (path is not null)
                {
                    await chat.ExportToAsync(path);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort by design (see the route's summary).
        }
    }
}
