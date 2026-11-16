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
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The venue-spread panel's state (T-253): the symbol text, the
/// time-frame and window knobs and the sampled table page — one row per
/// registered venue (the live quote, its cross-venue delta, the
/// perp-spot basis spark). The sample is on-demand: the command awaits
/// the service's off-UI pass (the ticker race under the wait budget and
/// the cache reads under the two-slot budget ride the pool, the
/// <see cref="IsSampling"/> flag carries the busy state), and the
/// published snapshot applies as one page. A venue that never answers
/// keeps its table slot as the honest dash row — never an error.
/// </summary>
/// <remarks>
/// The panel ViewModel lives as long as the application (a workspace
/// singleton), so its subscriptions mirror that lifetime and need no
/// explicit disposal, exactly like every workspace panel's ViewModel.
/// </remarks>
public sealed class VenueSpreadViewModel : ReactiveObject
{
    #region Constants

    /// <summary>The window knob's choices in bars (the basis series each venue reads over).</summary>
    public static readonly IReadOnlyList<int> WindowChoices = [50, 100, 200];

    /// <summary>The frame knob's choices in column order (the MtfTrend frames).</summary>
    internal static readonly IReadOnlyList<TimeFrame> FrameChoices =
        [TimeFrame.M15, TimeFrame.H1, TimeFrame.H4, TimeFrame.D1];

    private const string SampleFailedKey = "VenueSpread.SampleFailed";
    private const string NoAnswersKey = "VenueSpread.NoAnswers";
    private const string EmptySymbolKey = "VenueSpread.EmptySymbol";

    /// <summary>The one-based index of the default frame choice (H1) in <see cref="FrameChoices"/>.</summary>
    private const int FrameIndexH1 = 1;

    /// <summary>The starter symbol text (the panel opens honest: a real pair on every demo venue).</summary>
    private const string StarterSymbolText = "BTC/USDT";

    #endregion Constants

    #region Private Fields

    /// <summary>The cross-venue sampler the sample command awaits.</summary>
    private readonly VenueSpreadService _sampler;

    /// <summary>The panel's localization lane (the failure and no-answer notes format here).</summary>
    private readonly ILocalizationManager _localization;

    /// <summary>The panel's own logging lane (a failed pass lands here).</summary>
    private readonly ILogger<VenueSpreadViewModel> _logger;

    private string _symbolText = StarterSymbolText;

    private int _selectedFrameIndex = FrameIndexH1;

    private int _selectedWindowIndex = 1;

    private IReadOnlyList<VenueSpreadRowViewModel> _rows = [];

    private bool _hasRows;

    private bool _isSampling;

    private string? _note;

    private bool _hasNote;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The sampled symbol's text (the spot form or the settled perp
    /// form). A text the parser refuses answers the localized note on
    /// the next sample — the panel never silently substitutes.
    /// </summary>
    public string SymbolText
    {
        get => _symbolText;
        set => this.RaiseAndSetIfChanged(ref _symbolText, value);
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
    /// (50/100/200). Defaults to 100 — the basis series' span.
    /// </summary>
    public int SelectedWindowIndex
    {
        get => _selectedWindowIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedWindowIndex, Math.Max(0, Math.Min(value, WindowChoices.Count - 1)));
    }

    /// <summary>The table's display rows (one per registered venue, the fleet's order).</summary>
    public IReadOnlyList<VenueSpreadRowViewModel> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    /// <summary>Whether the page carries a sampled table (the empty state's flip).</summary>
    public bool HasRows
    {
        get => _hasRows;
        private set => this.RaiseAndSetIfChanged(ref _hasRows, value);
    }

    /// <summary>Whether a sample pass is running (the command disables itself; the UI stays free).</summary>
    public bool IsSampling
    {
        get => _isSampling;
        private set => this.RaiseAndSetIfChanged(ref _isSampling, value);
    }

    /// <summary>
    /// The status note line: a failure's reason, the no-answer honesty —
    /// the quiet line keeps hidden (<see cref="HasNote"/>). Localized at
    /// apply time (the chart's own error-line precedent).
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

    /// <summary>The sample command: the typed symbol, the standing knobs, one pass.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SampleCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the panel over the sampler: the command wires the on-demand
    /// pass. No subscription arms here — the venue fleet is fixed at
    /// composition and the pass is stateless (every sample answers
    /// fresh).
    /// </summary>
    public VenueSpreadViewModel(
        VenueSpreadService sampler,
        ILocalizationManager localization,
        ILogger<VenueSpreadViewModel> logger)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        SampleCommand = ReactiveCommand.CreateFromTask(HandleSampleAsync);
    }

    #endregion Constructor

    #region Sample

    /// <summary>
    /// Run one sample pass: the typed symbol, the standing knobs, the
    /// service's off-UI await. A refused text (the parser's rejection)
    /// and a failed pass both log and format the note — the standing
    /// table keeps its place.
    /// </summary>
    private async Task HandleSampleAsync(CancellationToken ct)
    {
        var symbolText = SymbolText?.Trim();
        if (string.IsNullOrEmpty(symbolText))
        {
            Note = _localization.GetString(EmptySymbolKey);
            return;
        }

        IsSampling = true;
        try
        {
            var snapshot = await _sampler.SampleAsync(
                symbolText,
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
            _logger.LogError(error, "Venue spread: the sample pass failed; the panel keeps its last table");
            Note = _localization.GetString(SampleFailedKey, error.Message);
        }
        finally
        {
            IsSampling = false;
        }
    }

    /// <summary>
    /// Apply one published snapshot: the rows and the note move together
    /// (a table and its honesty notes are one panel state — never a
    /// table half-applied). A pass where no venue answered says so on
    /// the note line: the rows stay (the venues are real), the dashes
    /// speak.
    /// </summary>
    public void ApplySnapshot(VenueSpreadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var culture = CultureInfo.CurrentUICulture;
        var rows = snapshot.Quotes
            .Zip(snapshot.Bases, static (quote, basis) => (quote, basis))
            .Select(pair => BuildRow(pair.quote, pair.basis, culture))
            .ToArray();

        Rows = rows;
        HasRows = rows.Length > 0;
        Note = rows.All(static row => row.LastText == VenueSpreadRowViewModel.DashText)
            ? _localization.GetString(NoAnswersKey)
            : null;
    }

    /// <summary>Build one display row: the precomputed texts, tints and spark points.</summary>
    private static VenueSpreadRowViewModel BuildRow(
        VenueQuote quote,
        VenueBasis basis,
        CultureInfo culture)
    {
        var cultureFormat = CultureInfo.InvariantCulture;
        var delta = quote.LastDeltaBps;
        var basisLast = basis.Samples.Count > 0 ? (double?)basis.Samples[^1].Bps : null;
        var basisText = basisLast is { } bps
            ? bps.ToString("+0.0;-0.0;0.0", cultureFormat)
            : VenueSpreadRowViewModel.DashText;

        return new VenueSpreadRowViewModel
        {
            Venue = quote.Venue,
            LastText = quote.Last is { } last
                ? WatchlistRowPresentation.FormatPrice(last, culture)
                : VenueSpreadRowViewModel.DashText,
            BidText = quote.Bid is { } bid
                ? WatchlistRowPresentation.FormatPrice(bid, culture)
                : VenueSpreadRowViewModel.DashText,
            AskText = quote.Ask is { } ask
                ? WatchlistRowPresentation.FormatPrice(ask, culture)
                : VenueSpreadRowViewModel.DashText,
            DeltaText = delta is { } answered
                ? answered.ToString("+0.0;-0.0;0.0", cultureFormat)
                : VenueSpreadRowViewModel.DashText,
            IsDeltaPositive = delta is > 0.0,
            IsDeltaNegative = delta is < 0.0,
            BasisText = basisText,
            IsBasisPositive = basisLast is > 0.0,
            IsBasisNegative = basisLast is < 0.0,
            BasisPoints = WatchlistRowPresentation.BuildSparklinePoints(
                [.. basis.Samples.Select(static sample => (decimal)sample.Bps)],
                BasisSparkWidth,
                BasisSparkHeight),
        };
    }

    /// <summary>The basis spark's canvas width (the row's basis column, logical pixels).</summary>
    private const double BasisSparkWidth = 120;

    /// <summary>The basis spark's canvas height (logical pixels).</summary>
    private const double BasisSparkHeight = 28;

    #endregion Sample
}
