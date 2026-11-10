namespace ZettaTrade.App.Views.Panels;

using Avalonia.Controls;

/// <summary>
/// The AI assistant panel (T-103): the extracted chat column the main
/// window's assistant column and the detached assistant window share. ONE
/// control instance exists — the detach moves it cross-window, so the
/// chat input keeps its identity and its focus affordance on either host.
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
}
