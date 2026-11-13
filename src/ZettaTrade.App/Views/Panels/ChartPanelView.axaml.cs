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
    /// Whether the panel renders its signal-rail strip (T-201): the primary
    /// panel keeps the default <c>true</c>; a multi-chart grid's secondary
    /// slot sets <c>false</c> at construction - the rail reads the
    /// workspace's signal feed (the strip is the primary's surface), and a
    /// slot repeating it under another symbol would show foreign rows. The
    /// collapse is one-way by design: the strip row's bound Min/Max clamps
    /// drop to local zeros - their constant sources publish once, so the
    /// locals stick for the instance's lifetime - and the TwoWay Height
    /// binding is left alone on purpose: a local Height zero would write
    /// back into the shared rail VM and corrupt the primary rail's
    /// persisted height (the persisted write clamps it, but the primary
    /// rail would still shrink). The track row's plain 8px height and the
    /// handle and the strip Border hide with it.
    /// </summary>
    public static readonly StyledProperty<bool> ShowSignalRailProperty =
        AvaloniaProperty.Register<ChartPanelView, bool>(nameof(ShowSignalRail), true);

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

    /// <summary>See <see cref="ShowSignalRailProperty"/>.</summary>
    public bool ShowSignalRail
    {
        get => GetValue(ShowSignalRailProperty);
        set => SetValue(ShowSignalRailProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == ShowSignalRailProperty && !ShowSignalRail)
        {
            CollapseSignalRail();
        }
    }

    /// <summary>
    /// The slot's one-way rail collapse (T-201), fired when
    /// <see cref="ShowSignalRailProperty"/> turns false: the strip row's
    /// bound Min/Max clamps drop to local zeros and the whole strip - the
    /// splitter track, the handle, the Border - hides. IsVisible alone
    /// would not be enough: the track row's plain 8px height and the
    /// strip row's persisted-height minimum would keep eating panel
    /// height, and the T-038 row binding would keep its TwoWay writeback
    /// (so the Height itself is left untouched - see the property's doc).
    /// The collapse never reverts: slots construct rail-less by design.
    /// </summary>
    private void CollapseSignalRail()
    {
        var rows = PanelGrid.RowDefinitions;
        rows[4].Height = new GridLength(0);
        rows[5].MinHeight = 0;
        rows[5].MaxHeight = 0;
        RailRowHandle.IsVisible = false;
        RailBorder.IsVisible = false;
    }

    /// <summary>The chart control itself: the HUD's frame-tap drain and the export routes read it here (the named control lives inside this panel's namescope now).</summary>
    internal CandlestickChart ChartControl => CandlesChart;
}
