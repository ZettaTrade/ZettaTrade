namespace ZettaTrade.App.Views;

using Avalonia.Controls;

/// <summary>
/// The detached assistant window (T-103): a trivial chrome shell; the
/// coordinator owns its content (the assistant panel's one control
/// instance), placement and lifetime.
/// </summary>
public partial class DetachedAssistantWindow : Window
{
    /// <summary>
    /// Create a new DetachedAssistantWindow instance.
    /// </summary>
    public DetachedAssistantWindow()
    {
        InitializeComponent();
    }
}
