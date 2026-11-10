namespace ZettaTrade.App.Views.Panels;

using Avalonia.Controls;

/// <summary>
/// The order book panel (T-103): the extracted book column (the ladder and
/// the cumulative depth profile) the main window's workspace grid and the
/// detached book window share. ONE control instance exists — the detach
/// moves it cross-window, so the level-press route the window code-behind
/// armed once keeps working on either host.
/// </summary>
public partial class OrderBookPanelView : UserControl
{
    /// <summary>
    /// Create a new OrderBookPanelView instance.
    /// </summary>
    public OrderBookPanelView()
    {
        InitializeComponent();
    }
}
