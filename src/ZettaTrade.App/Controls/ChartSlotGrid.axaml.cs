namespace ZettaTrade.App.Controls;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views.Panels;

/// <summary>
/// The workspace's multi-chart grid (T-201): the chart column's layout
/// control that hosts the primary panel's swap host and the slot panels.
/// The layout is code-built ("rows of stacks"): the single mode is one
/// cell, the 1x2 pair puts the primary and slot 1 side by side behind one
/// draggable slot-column seam, and the 2x2 grid stacks two such rows
/// behind one draggable top-row seam — both rows keep the same shared
/// slot column, so the vertical seams stay one aligned line. Every mode
/// change rebuilds the content in place: the primary's swap host moves
/// with it (the detach coordinator's reference never changes — the host
/// Border is reparented, the ONE chart panel inside it stays), and the
/// slot panels are created once per slot ViewModel and re-placed.
/// The seams persist through the layout ViewModel: the slot column's
/// width and the top row's height ride its TwoWay-bound properties (the
/// handles' drags flow in, a settled drag persists once), so the grid's
/// definitions subscribe both directions in code — the T-175 discipline:
/// no markup binding ever exists on the code-built tree.
/// </summary>
public partial class ChartSlotGrid : UserControl
{
    /// <summary>
    /// The layout ViewModel the grid mirrors: the mode drives the
    /// rebuilds, the seam properties ride the rebuilt rows and columns.
    /// </summary>
    public static readonly StyledProperty<WorkspaceChartsViewModel?> ChartsProperty =
        AvaloniaProperty.Register<ChartSlotGrid, WorkspaceChartsViewModel?>(nameof(Charts));

    /// <summary>Thickness of the fixed seam track columns and rows (the resize handles ride them).</summary>
    private const double SeamThickness = 8;

    private static readonly GridLength SeamTrack = new(SeamThickness);

    /// <summary>The mirrored layout ViewModel.</summary>
    private WorkspaceChartsViewModel? _chartsMirror;

    /// <summary>The mirrored mode's subscription: a change rebuilds the grid.</summary>
    private IDisposable? _layoutSubscription;

    /// <summary>The primary panel's swap host (T-103), handed over by the window once: the rebuilt layouts place it, the detach coordinator's reference never changes.</summary>
    private Border? _primaryContent;

    /// <summary>Every created slot panel lands here; the window wires the routes once per panel.</summary>
    private readonly Subject<ChartPanelView> _slotPanelsAdded = new();

    /// <summary>The created slot panels by their slot ViewModel: reused across rebuilds, never torn down in-session.</summary>
    private readonly Dictionary<ChartViewModel, ChartPanelView> _slotPanelCache = [];

    /// <summary>The placed slot panels, swapped wholesale per rebuild: the HUD's frame drain reads it from the sampler thread, so it is always a whole immutable array.</summary>
    private IReadOnlyList<ChartPanelView> _placedSlotPanels = [];

    /// <summary>The rebuilt layout's live rides (the seam rows and columns' two-direction subscriptions); disposed at every rebuild.</summary>
    private readonly List<IDisposable> _layoutRides = [];

    /// <summary>Create a new ChartSlotGrid instance.</summary>
    public ChartSlotGrid()
    {
        InitializeComponent();

        this.GetObservable(ChartsProperty).Subscribe(charts =>
        {
            _layoutSubscription?.Dispose();
            _layoutSubscription = null;
            _chartsMirror = charts;

            if (charts is null)
            {
                return;
            }

            // A mode change rebuilds the grid; the subscribe-time emission
            // is skipped — the attach walk below owns the first rebuild
            // (the window wires the slot routes before any panel can be
            // created, so the attach rebuild is the first wired one).
            _layoutSubscription = charts.WhenAnyValue(x => x.Layout)
                .Skip(1)
                .Subscribe(_ =>
                {
                    if (this.IsAttachedToVisualTree())
                    {
                        Rebuild();
                    }
                });
        });
    }

    /// <summary>See <see cref="ChartsProperty"/>.</summary>
    public WorkspaceChartsViewModel? Charts
    {
        get => GetValue(ChartsProperty);
        set => SetValue(ChartsProperty, value);
    }

    /// <summary>
    /// The primary panel's swap host, handed over once (the window's
    /// constructor): every rebuilt layout places it into the primary
    /// cell — the detach coordinator's inline-host reference survives the
    /// moves untouched (the Border is reparented, never rebuilt).
    /// </summary>
    public Border? PrimaryContent
    {
        get => _primaryContent;
        set
        {
            // Release the XAML parking spot: the host Border may arrive as
            // this control's XAML Content; the rebuilt layouts take it as
            // a grid child instead.
            if (ReferenceEquals(Content, value))
            {
                Content = null;
            }

            _primaryContent = value;
        }
    }

    /// <summary>The placed slot panels (the HUD's frame drain sums them with the primary's count).</summary>
    public IReadOnlyList<ChartPanelView> SlotPanels => _placedSlotPanels;

    /// <summary>Every slot panel this control created; the window wires the routes once per panel.</summary>
    public IObservable<ChartPanelView> SlotPanelsAdded => _slotPanelsAdded;

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The first rebuild rides the attach: the window's route wiring
        // lives by then, so the attach walk's new slot panels are wired
        // the moment they are created.
        Rebuild();
    }

    /// <summary>Rebuild the layout for the mirrored mode: tear the old seams down, build the new shape in place.</summary>
    private void Rebuild()
    {
        var charts = _chartsMirror;
        if (charts is null)
        {
            return;
        }

        foreach (var ride in _layoutRides)
        {
            ride.Dispose();
        }

        _layoutRides.Clear();

        var placed = new List<ChartPanelView>();
        Content = charts.Layout switch
        {
            ChartLayoutMode.OneByTwo => BuildOneByTwo(charts, placed),
            ChartLayoutMode.TwoByTwo => BuildTwoByTwo(charts, placed),
            _ => BuildSingle(),
        };

        // The snapshot swaps wholesale: an in-flight drain reads either
        // the old or the new immutable array, never a torn one.
        _placedSlotPanels = placed;
    }

    /// <summary>The single-chart layout: one cell, the primary's swap host fills it.</summary>
    private Grid BuildSingle()
    {
        var grid = NewGrid(
            [new RowDefinition(GridLength.Star)],
            [new ColumnDefinition(GridLength.Star)]);
        PlacePrimary(grid, 0, 0);
        return grid;
    }

    /// <summary>
    /// The 1x2 pair: the primary and slot 1 side by side, the draggable
    /// slot-column seam between them (the persisted slot column width —
    /// the shared seam the 2x2 grid aligns its rows on).
    /// </summary>
    private Grid BuildOneByTwo(WorkspaceChartsViewModel charts, List<ChartPanelView> placed)
    {
        var slotColumn = new ColumnDefinition(charts.SlotColumnWidth)
        {
            // The drag-side clamp: the splitter itself stops at the
            // usable minimum (one headered chart face stays readable).
            MinWidth = UiSettingsKeys.MinChartSlotColumnWidth,
        };
        var grid = NewGrid(
            [new RowDefinition(GridLength.Star)],
            [new ColumnDefinition(GridLength.Star), new ColumnDefinition(SeamTrack), slotColumn]);

        PlacePrimary(grid, 0, 0);
        PlaceHandle(grid, new ColumnResizeHandle(), 0, 1, "ChartSlotColumnHandle");
        PlaceSlot(grid, charts.Slot1, 1, 0, 2, placed);
        RideSlotSeam(charts, slotColumn);
        return grid;
    }

    /// <summary>
    /// The 2x2 grid: two stacked rows (the primary and slot 1 on top,
    /// slots 2 and 3 under them), the draggable top-row seam between them
    /// and a slot-column seam per row — both rows keep the same shared
    /// slot column width, so the vertical seams stay one aligned line.
    /// </summary>
    private Grid BuildTwoByTwo(WorkspaceChartsViewModel charts, List<ChartPanelView> placed)
    {
        var topRowHeight = new RowDefinition(charts.TopRowHeight)
        {
            // The drag-side clamp: the horizontal splitter itself stops at
            // the usable minimum (one headered chart face stays readable).
            MinHeight = UiSettingsKeys.MinChartGridTopRowHeight,
        };
        var topSlotColumn = new ColumnDefinition(charts.SlotColumnWidth)
        {
            MinWidth = UiSettingsKeys.MinChartSlotColumnWidth,
        };
        var bottomSlotColumn = new ColumnDefinition(charts.SlotColumnWidth)
        {
            MinWidth = UiSettingsKeys.MinChartSlotColumnWidth,
        };

        var topRow = NewGrid(
            [new RowDefinition(GridLength.Star)],
            [new ColumnDefinition(GridLength.Star), new ColumnDefinition(SeamTrack), topSlotColumn]);
        PlacePrimary(topRow, 0, 0);
        PlaceHandle(topRow, new ColumnResizeHandle(), 0, 1, "ChartTopSlotColumnHandle");
        PlaceSlot(topRow, charts.Slot1, 1, 0, 2, placed);

        var bottomRow = NewGrid(
            [new RowDefinition(GridLength.Star)],
            [new ColumnDefinition(GridLength.Star), new ColumnDefinition(SeamTrack), bottomSlotColumn]);
        PlaceSlot(bottomRow, charts.Slot2, 2, 0, 0, placed);
        PlaceHandle(bottomRow, new ColumnResizeHandle(), 0, 1, "ChartBottomSlotColumnHandle");
        PlaceSlot(bottomRow, charts.Slot3, 3, 0, 2, placed);

        var root = NewGrid(
            [topRowHeight, new RowDefinition(SeamTrack), new RowDefinition(GridLength.Star)],
            [new ColumnDefinition(GridLength.Star)]);
        Grid.SetRow(topRow, 0);
        root.Children.Add(topRow);
        PlaceHandle(root, new RowResizeHandle(), 1, 0, "ChartGridRowHandle");
        Grid.SetRow(bottomRow, 2);
        root.Children.Add(bottomRow);

        RideSlotSeam(charts, topSlotColumn);
        RideSlotSeam(charts, bottomSlotColumn);
        RideTopRow(charts, topRowHeight);
        return root;
    }

    /// <summary>Place the primary's swap host into a cell, reparenting it from wherever the last layout parked it.</summary>
    private void PlacePrimary(Grid grid, int row, int column)
    {
        var content = _primaryContent;
        if (content is null)
        {
            return;
        }

        if (content.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(content);
        }

        Grid.SetRow(content, row);
        Grid.SetColumn(content, column);
        grid.Children.Add(content);
    }

    /// <summary>Create one slot panel (once per slot ViewModel) and place it into a cell; the window wires its routes.</summary>
    private void PlaceSlot(
        Grid grid,
        ChartViewModel? slot,
        int slotNumber,
        int row,
        int column,
        List<ChartPanelView> placed)
    {
        // The layout ViewModel guarantees the mode's slots exist before
        // its property raise (the rebuild never sees a missing slot); the
        // nulls keep the designer's route honest.
        if (slot is null)
        {
            return;
        }

        if (!_slotPanelCache.TryGetValue(slot, out var panel))
        {
            panel = new ChartPanelView(slot)
            {
                ShowSignalRail = false, // The rail strip is the primary's surface; a slot repeating it would show foreign rows.
            };
            panel.Name = $"ChartSlot{slotNumber}Panel";
            AutomationProperties.SetAutomationId(panel, $"ChartSlot{slotNumber}Panel");
            _slotPanelCache[slot] = panel;
            _slotPanelsAdded.OnNext(panel);
        }

        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        placed.Add(panel);
    }

    /// <summary>Place a resize handle into a cell and pin its automation id.</summary>
    private static void PlaceHandle(Grid grid, Avalonia.Controls.Primitives.TemplatedControl handle, int row, int column, string automationId)
    {
        Grid.SetRow(handle, row);
        Grid.SetColumn(handle, column);
        AutomationProperties.SetAutomationId(handle, automationId);
        grid.Children.Add(handle);
    }

    /// <summary>
    /// Ride a slot column both directions: the ViewModel's width applies
    /// into the definition, the handles' dragged widths flow back into
    /// the ViewModel (whose pipeline persists a settled drag). The echoes
    /// converge — the ViewModel absorbs its own values
    /// (RaiseAndSetIfChanged), so a ride never loops.
    /// </summary>
    private void RideSlotSeam(WorkspaceChartsViewModel charts, ColumnDefinition slotColumn)
    {
        slotColumn.Width = charts.SlotColumnWidth;
        _layoutRides.Add(charts.WhenAnyValue(x => x.SlotColumnWidth)
            .Subscribe(width => slotColumn.Width = width));
        _layoutRides.Add(slotColumn.GetObservable(ColumnDefinition.WidthProperty)
            .Skip(1)
            .Subscribe(width => charts.SlotColumnWidth = width));
    }

    /// <summary>See <see cref="RideSlotSeam"/>; the 2x2 top row's height rides the same both-direction shape.</summary>
    private void RideTopRow(WorkspaceChartsViewModel charts, RowDefinition topRow)
    {
        topRow.Height = charts.TopRowHeight;
        _layoutRides.Add(charts.WhenAnyValue(x => x.TopRowHeight)
            .Subscribe(height => topRow.Height = height));
        _layoutRides.Add(topRow.GetObservable(RowDefinition.HeightProperty)
            .Skip(1)
            .Subscribe(height => charts.TopRowHeight = height));
    }

    private static Grid NewGrid(IReadOnlyList<RowDefinition> rowDefinitions, IReadOnlyList<ColumnDefinition> columnDefinitions)
    {
        var grid = new Grid();
        foreach (var definition in rowDefinitions)
        {
            grid.RowDefinitions.Add(definition);
        }

        foreach (var definition in columnDefinitions)
        {
            grid.ColumnDefinitions.Add(definition);
        }

        return grid;
    }
}
