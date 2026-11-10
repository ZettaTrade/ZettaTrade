namespace ZettaTrade.App.Views.Panels;

using Avalonia.Controls;

/// <summary>
/// The detached-slot note (T-103): what a panel's inline host shows while
/// the panel lives in its own window — one localized sentence, no state.
/// </summary>
public partial class PanelDetachedNote : UserControl
{
    /// <summary>
    /// Create a new PanelDetachedNote instance.
    /// </summary>
    public PanelDetachedNote()
    {
        InitializeComponent();
    }
}
