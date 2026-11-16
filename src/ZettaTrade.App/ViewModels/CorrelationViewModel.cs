namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The correlation matrix panel's state (T-252): the watchlist picker, the
/// time-frame and window knobs and the computed matrix page. The compute
/// is on-demand — the command awaits the service's off-UI pass (the
/// cache-read budget and the matrix assembly ride the pool, the
/// <see cref="IsComputing"/> flag carries the busy state), and the
/// published snapshot applies as one page: the symbol headers, the rows,
/// the honest dashes. A cell press routes the pair into the chart's
/// compare view (T-124) — the member that is not the chart's standing
/// anchor joins the layers; a refusal surfaces on the chart's own error
/// line (the compare seam's own discipline).
/// </summary>
/// <remarks>
/// The panel ViewModel lives as long as the application (a workspace
/// singleton), so its subscriptions mirror that lifetime and need no
/// explicit disposal, exactly like every workspace panel's ViewModel.
/// </remarks>
public sealed class CorrelationViewModel : ReactiveObject
{
    #region Constants

    /// <summary>The window knob's choices in bars (the returns each pair correlates over).</summary>
    public static readonly IReadOnlyList<int> WindowChoices = [50, 100, 200];

    /// <summary>The frame knob's choices in column order (the MtfTrend frames).</summary>
    internal static readonly IReadOnlyList<TimeFrame> FrameChoices =
        [TimeFrame.M15, TimeFrame.H1, TimeFrame.H4, TimeFrame.D1];

    private const string TruncatedKey = "Correlation.Truncated";
    private const string ComputeFailedKey = "Correlation.ComputeFailed";
    private const string EmptyListKey = "Correlation.EmptyList";

    #endregion Constants

    #region Private Fields

    /// <summary>The correlation computer the compute command awaits.</summary>
    private readonly CorrelationService _correlation;

    /// <summary>The user-managed watchlists' catalog: the picker's roster source.</summary>
    private readonly WatchlistCatalogService _catalog;

    /// <summary>The chart ViewModel whose compare layers a cell press routes into.</summary>
    private readonly ViewModels.ChartViewModel _chart;

    /// <summary>The panel's localization lane (the failure and truncation notes format here).</summary>
    private readonly ILocalizationManager _localization;

    /// <summary>The panel's own logging lane (a failed pass lands here).</summary>
    private readonly ILogger<CorrelationViewModel> _logger;

    private IReadOnlyList<string> _listNames = [];

    private int _selectedListIndex;

    private int _selectedFrameIndex = FrameIndexH1;

    private int _selectedWindowIndex = 1;

    private IReadOnlyList<string> _symbols = [];

    private IReadOnlyList<CorrelationRowViewModel> _rows = [];

    private bool _hasRows;

    private bool _isComputing;

    private string? _note;

    private bool _hasNote;

    #endregion Private Fields

    #region Properties

    /// <summary>The one-based index of the default frame choice (H1) in <see cref="FrameChoices"/>.</summary>
    private const int FrameIndexH1 = 1;

    /// <summary>The watchlist picker's roster (the catalog document's list names, document order).</summary>
    public IReadOnlyList<string> ListNames
    {
        get => _listNames;
        private set => this.RaiseAndSetIfChanged(ref _listNames, value);
    }

    /// <summary>
    /// The watchlist picker's index shim: the combo's items bind the
    /// <see cref="ListNames"/> texts directly (the chart-type selector's
    /// precedent). The default rides the document's active list; a
    /// document change re-clamps the standing index.
    /// </summary>
    public int SelectedListIndex
    {
        get => _selectedListIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedListIndex, Math.Max(0, Math.Min(value, _listNames.Count - 1)));
    }

    /// <summary>
    /// The frame knob's index shim: 0..3 over M15/H1/H4/D1 (the combo's
    /// static localized items bind by index). Defaults to H1 — the frame
    /// a fresh cache most plausibly carries depth for.
    /// </summary>
    public int SelectedFrameIndex
    {
        get => _selectedFrameIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedFrameIndex, Math.Max(0, Math.Min(value, FrameChoices.Count - 1)));
    }

    /// <summary>
    /// The window knob's index shim over <see cref="WindowChoices"/>
    /// (50/100/200). Defaults to 100 — half a full window of overlap
    /// stays the honest pair minimum.
    /// </summary>
    public int SelectedWindowIndex
    {
        get => _selectedWindowIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedWindowIndex, Math.Max(0, Math.Min(value, WindowChoices.Count - 1)));
    }

    /// <summary>The matrix's symbol headers (the column order the rows read in).</summary>
    public IReadOnlyList<string> Symbols
    {
        get => _symbols;
        private set => this.RaiseAndSetIfChanged(ref _symbols, value);
    }

    /// <summary>The matrix's display rows (one per symbol, the cells in column order).</summary>
    public IReadOnlyList<CorrelationRowViewModel> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    /// <summary>Whether the page carries a computed matrix (the empty state's flip).</summary>
    public bool HasRows
    {
        get => _hasRows;
        private set => this.RaiseAndSetIfChanged(ref _hasRows, value);
    }

    /// <summary>Whether a compute pass is running (the command disables itself; the UI stays free).</summary>
    public bool IsComputing
    {
        get => _isComputing;
        private set => this.RaiseAndSetIfChanged(ref _isComputing, value);
    }

    /// <summary>
    /// The status note line: the truncation honesty after a capped pass,
    /// an empty list's refusal, a failure's reason — the quiet line keeps
    /// hidden (<see cref="HasNote"/>). Localized at apply time (the
    /// chart's own error-line precedent).
    /// </summary>
    public string? Note
    {
        get => _note;
        private set
        {
            this.RaiseAndSetIfChanged(ref _note, value);
            HasNote = value is not null;
        }
    }

    /// <summary>Whether the note line speaks (the string's visibility companion).</summary>
    public bool HasNote
    {
        get => _hasNote;
        private set => this.RaiseAndSetIfChanged(ref _hasNote, value);
    }

    /// <summary>The compute command: the chosen list's symbols, the standing knobs, one pass.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ComputeCommand { get; }

    /// <summary>The cell-press route: the pair joins the chart's compare view (T-124).</summary>
    public ReactiveCommand<CorrelationCellViewModel, RxVoid> RouteCellCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the panel over the computer, the catalog and the chart: the
    /// catalog's document stream re-plans the picker (the starter document
    /// stands until the stored one applies), the commands wire the compute
    /// and the compare route.
    /// </summary>
    public CorrelationViewModel(
        CorrelationService correlation,
        WatchlistCatalogService catalog,
        ViewModels.ChartViewModel chart,
        ILocalizationManager localization,
        ILogger<CorrelationViewModel> logger)
    {
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        catalog.Changes
            .ObserveOnUiThread()
            .Subscribe(
                ApplyDocument,
                ex => logger.LogError(ex, "The watchlist document stream stopped unexpectedly"));
        ApplyDocument(catalog.Current);

        ComputeCommand = ReactiveCommand.CreateFromTask(HandleComputeAsync);
        RouteCellCommand = ReactiveCommand.CreateFromTask<CorrelationCellViewModel, RxVoid>(RouteCellAsync);
    }

    #endregion Constructor

    #region Document Apply

    /// <summary>
    /// Apply one watchlist document: the picker's roster refreshes and the
    /// standing index re-clamps onto it (the active list keeps the pick
    /// when it still exists — a renamed roster falls back to the first).
    /// The standing matrix stays: a roster change never wipes a computed
    /// page (the next compute replaces it).
    /// </summary>
    private void ApplyDocument(WatchlistDocument document)
    {
        ListNames = [.. document.Lists.Select(static list => list.Name)];
        var active = -1;
        for (var i = 0; i < document.Lists.Count; i++)
        {
            if (string.Equals(document.Lists[i].Name, document.ActiveList, StringComparison.Ordinal))
            {
                active = i;
                break;
            }
        }

        _selectedListIndex = active >= 0 ? active : 0;
        this.RaisePropertyChanged(nameof(SelectedListIndex));
    }

    #endregion Document Apply

    #region Compute

    /// <summary>
    /// Run one compute pass: the chosen list's symbols (the picker's
    /// standing index), the standing knobs, the service's off-UI await.
    /// An empty list answers the honest empty state; a failed pass logs
    /// and formats the note — the standing matrix keeps its place.
    /// </summary>
    private async Task HandleComputeAsync(CancellationToken ct)
    {
        var listName = _selectedListIndex >= 0 && _selectedListIndex < _listNames.Count
            ? _listNames[_selectedListIndex]
            : null;
        var symbols = listName is null
            ? []
            : _catalog.Current.Lists.FirstOrDefault(
                    list => string.Equals(list.Name, listName, StringComparison.Ordinal))?.Symbols
                ?? [];

        if (symbols.Count == 0)
        {
            Rows = [];
            Symbols = [];
            HasRows = false;
            Note = _localization.GetString(EmptyListKey);
            return;
        }

        IsComputing = true;
        try
        {
            var snapshot = await _correlation.ComputeAsync(
                symbols,
                FrameChoices[_selectedFrameIndex],
                WindowChoices[_selectedWindowIndex],
                ct);
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            // The pass rides the command's own lifetime; a cancellation
            // is a shutdown, never a fault.
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Correlation: the compute pass failed; the panel keeps its last matrix");
            Note = _localization.GetString(ComputeFailedKey, error.Message);
        }
        finally
        {
            IsComputing = false;
        }
    }

    /// <summary>
    /// Apply one published snapshot: the headers, the rows and the note
    /// move together (a matrix, its order and its honesty notes are one
    /// panel state — never a table half-applied).
    /// </summary>
    public void ApplySnapshot(CorrelationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Symbols = [.. snapshot.Symbols];
        var rows = new CorrelationRowViewModel[snapshot.Symbols.Count];
        for (var i = 0; i < snapshot.Symbols.Count; i++)
        {
            var cells = new CorrelationCellViewModel[snapshot.Symbols.Count];
            for (var j = 0; j < snapshot.Symbols.Count; j++)
            {
                var value = snapshot.Matrix[i, j];
                cells[j] = new CorrelationCellViewModel(
                    FormatCell(value),
                    IsRoutable: i != j && value is not null,
                    IsPositive: value > 0.0,
                    IsNegative: value < 0.0,
                    RowSymbol: snapshot.Symbols[i],
                    ColumnSymbol: snapshot.Symbols[j]);
            }

            rows[i] = new CorrelationRowViewModel
            {
                Symbol = snapshot.Symbols[i],
                Cells = cells,
            };
        }

        Rows = rows;
        HasRows = rows.Length > 0;
        Note = snapshot.Truncated
            ? _localization.GetString(TruncatedKey, CorrelationService.MaxSymbols)
            : null;
    }

    /// <summary>
    /// Format one cell: the signed two-decimal coefficient (the heatmap
    /// caption's explicit-sign form) or the honest dash.
    /// </summary>
    private static string FormatCell(double? value) => value is { } numeric
        ? numeric.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)
        : CorrelationCellViewModel.DashText;

    #endregion Compute

    #region Cell Route

    /// <summary>
    /// Route one cell's pair into the chart's compare view (T-124): the
    /// member that is not the chart's standing anchor joins the layers —
    /// the anchor itself never self-compares (the compare seam's own
    /// refusal answers first). When the anchor carries neither member the
    /// column symbol routes (the row is the subject, the column the
    /// comparand — the press compares the pair against whatever the chart
    /// holds). A text the active exchange's parser never accepts stays
    /// unrouted (the refuse-not-default discipline).
    /// </summary>
    public async Task<RxVoid> RouteCellAsync(CorrelationCellViewModel? cell)
    {
        if (cell is null || !cell.IsRoutable)
        {
            return RxVoid.Default;
        }

        var anchor = _chart.SelectedSymbol;
        var routeText = anchor is { } existing
            && string.Equals(existing.Normalized, cell.ColumnSymbol, StringComparison.Ordinal)
                ? cell.RowSymbol
                : cell.ColumnSymbol;

        Symbol? resolved = null;
        try
        {
            resolved = Symbol.Parse(routeText);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning(
                "Correlation: the cell text '{SymbolText}' does not parse on the active exchange; the press stays unrouted",
                routeText);
        }

        if (resolved is not null)
        {
            await _chart.AddCompareAsync(resolved);
        }

        return RxVoid.Default;
    }

    #endregion Cell Route
}
