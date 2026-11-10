namespace ZettaTrade.App.Views;

using Avalonia.Controls;

/// <summary>
/// The detached chart window (T-103): a trivial chrome shell; the
/// coordinator owns its content (the chart panel's one control instance),
/// placement and lifetime.
/// </summary>
public partial class DetachedChartWindow : Window
{
    /// <summary>
    /// Create a new DetachedChartWindow instance.
    /// </summary>
    public DetachedChartWindow()
    {
        InitializeComponent();
    }
}
