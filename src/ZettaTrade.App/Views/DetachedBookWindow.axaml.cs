namespace ZettaTrade.App.Views;

using Avalonia.Controls;

/// <summary>
/// The detached order book window (T-103): a trivial chrome shell; the
/// coordinator owns its content (the order book panel's one control
/// instance), placement and lifetime.
/// </summary>
public partial class DetachedBookWindow : Window
{
    /// <summary>
    /// Create a new DetachedBookWindow instance.
    /// </summary>
    public DetachedBookWindow()
    {
        InitializeComponent();
    }
}
