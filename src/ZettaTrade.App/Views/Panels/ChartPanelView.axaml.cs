namespace ZettaTrade.App.Views.Panels;

using Avalonia;
using Avalonia.Controls;
using ZettaTrade.UI.Charting;

/// <summary>
/// The chart workspace panel (T-103): the extracted chart column the main
/// window's workspace grid and the detached chart window share. ONE control
/// instance exists — the detach moves it cross-window, so every named
/// control, subscription and export route the window code-behind armed
/// once keeps working on either host.
/// </summary>
public partial class ChartPanelView : UserControl
{
    /// <summary>
    /// The HUD frame-tap gate (T-108): the main window binds it to the HUD
    /// overlay's visibility; a detached chart window leaves it off. While
    /// the chart lives in its own window the HUD's chart-tap readout
    /// freezes (the HUD is a main-window overlay; the detached window's
    /// own HUD is out of T-103 scope).
    /// </summary>
    public static readonly StyledProperty<bool> HudFrameTapSourceProperty =
        AvaloniaProperty.Register<ChartPanelView, bool>(nameof(HudFrameTapSource));

    /// <summary>
    /// Create a new ChartPanelView instance.
    /// </summary>
    public ChartPanelView()
    {
        InitializeComponent();
    }

    /// <summary>See <see cref="HudFrameTapSourceProperty"/>.</summary>
    public bool HudFrameTapSource
    {
        get => GetValue(HudFrameTapSourceProperty);
        set => SetValue(HudFrameTapSourceProperty, value);
    }

    /// <summary>The chart control itself: the HUD's frame-tap drain and the export routes read it here (the named control lives inside this panel's namescope now).</summary>
    internal CandlestickChart ChartControl => CandlesChart;
}
