namespace ZettaTrade.App.Views.Panels;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ReactiveUI;
using ZettaTrade.App.ViewModels;
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
    /// Create the chart workspace panel around its view model (T-175): the
    /// DataContext is a LocalValue from the panel's own construction, so the
    /// subtree's compiled bindings evaluate against the chart VM from their
    /// very first pass. The XAML-created alternative let the loader wire the
    /// inherited MainViewModel into the unattached panel ahead of every
    /// tree link, cast-erroring the whole subtree on startup (the noise the
    /// T-175 sweep kills); the parameterless route below keeps the designer
    /// preview working and the T-175 counterfactual reproducible.
    /// </summary>
    public ChartPanelView(ChartViewModel? chart)
    {
        DataContext = chart;

        InitializeComponent();
    }

    /// <summary>
    /// The bare construction (the designer's route): the caller assigns the
    /// DataContext later; the startup cast-noise shape lives here.
    /// </summary>
    public ChartPanelView()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The HUD frame-tap gate (T-108), carried by the panel itself since
        // the T-175 code-created construction took it off the window's
        // axaml: while the panel hosts in a window whose DataContext is a
        // MainViewModel the gate follows that window's HUD visibility;
        // elsewhere (the detached chart window carries no DataContext by
        // design) no binding installs and the gate keeps its false default
        // - the detached readout stays frozen, as T-108 pinned.
        _hudGate?.Dispose();
        _hudGate = null;
        if (this.FindAncestorOfType<Window>()?.DataContext is MainViewModel main)
        {
            _hudGate = this.Bind(HudFrameTapSourceProperty, main.WhenAnyValue(x => x.Hud.IsVisible));
        }
    }

    /// <summary>The HUD gate's live binding, kept so the detach handback can swap it.</summary>
    private IDisposable? _hudGate;

    /// <summary>See <see cref="HudFrameTapSourceProperty"/>.</summary>
    public bool HudFrameTapSource
    {
        get => GetValue(HudFrameTapSourceProperty);
        set => SetValue(HudFrameTapSourceProperty, value);
    }

    /// <summary>The chart control itself: the HUD's frame-tap drain and the export routes read it here (the named control lives inside this panel's namescope now).</summary>
    internal CandlestickChart ChartControl => CandlesChart;
}
