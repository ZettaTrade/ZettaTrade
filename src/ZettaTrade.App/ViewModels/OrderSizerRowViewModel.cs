namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

// The Primitives namespace carries only RxVoid here; importing it outright
// would make every IObservable .Select ambiguous between System.Reactive
// and ReactiveUI's own LinqExtensions — the alias keeps RxVoid alone.
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The order ticket's quick-size row (T-079): presets of the available
/// balance (25/50/100 %) and a risk-percent proposal anchored on the
/// distance from the ticket's execution price to its stop price. The row
/// only PROPOSES quantities — it routes its proposal into the ticket's
/// existing quantity field (the very field the submit pipeline parses
/// with its own invariant-culture text parsing, validation and risk
/// checks), and never places or alters an order. Sizing rides ONLY
/// captured snapshots (the balance moment and the market reference close,
/// captured when the ticket opens, on a symbol commit or through the
/// row's deliberate refresh) plus the ticket's own inputs: zero network
/// calls at sizing time. The row keeps itself hidden for instruments
/// without round-size metadata and for non-pairs (stocks without a quote
/// asset); the risk-percent mode appears only for order types that carry
/// a stop context; a stale snapshot is displayed plainly (its capture
/// time) — never wrongly claimed fresh, and only a deliberate refresh
/// brings it current.
/// </summary>
/// <remarks>
/// Order-shape rules, decided deliberately:
/// <list type="bullet">
/// <item><b>Balance presets</b> are available whenever the row is visible:
/// a sell spends captured BASE custody directly, a buy converts captured
/// QUOTE custody at the entry estimate — the ticket's own execution price
/// when the type carries one (limit, stop-limit, the OCO take-profit
/// leg), the captured reference close otherwise (market, trailing
/// stop).</item>
/// <item><b>Risk percent</b> anchors on the stop distance: a
/// stop-limit's |limit − trigger|, a trailing stop's trail distance (the
/// trigger drags exactly that far behind the entry). A market order and a
/// plain limit carry no stop price — the mode stays hidden for them; the
/// OCO pair has two conflicting legs and no single honest stop anchor —
/// hidden there too. The risk budget is quote-denominated on both
/// sides.</item>
/// </list>
/// </remarks>
public sealed class OrderSizerRowViewModel : ReactiveObject, IDisposable
{
    #region Resx Keys

    private const string SnapshotDetailKey = "NewOrder.SizerSnapshotDetail";
    private const string SnapshotAssetMissingKey = "NewOrder.SizerAssetMissing";
    private const string NoProposalKey = "NewOrder.SizerNoProposal";

    #endregion Resx Keys

    #region Constants

    /// <summary>The 25 % preset share of the captured balance.</summary>
    private const decimal QuarterFraction = 0.25m;

    /// <summary>The 50 % preset share of the captured balance.</summary>
    private const decimal HalfFraction = 0.50m;

    /// <summary>The 100 % preset share of the captured balance.</summary>
    private const decimal WholeFraction = 1.00m;

    #endregion Constants

    #region Private Fields

    private readonly PositionSizingService _sizing;
    private readonly ISettingsService _settings;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<OrderSizerRowViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();

    /// <summary>Quantity texts this row proposes; the ticket writes them into its own quantity field.</summary>
    private readonly Subject<string> _quantityProposals = new();

    /// <summary>Symbol commits (metadata-known pairs only) that demand a fresh capture.</summary>
    private readonly Subject<Symbol> _symbolCommitted = new();

    /// <summary>One emission per capture publish; the tests' deterministic seam.</summary>
    private readonly Subject<RxVoid> _capturesPublished = new();

    /// <summary>The instrument the current context belongs to; null while hidden.</summary>
    private Symbol? _committedSymbol;

    /// <summary>The committed instrument's round-size metadata; null while hidden.</summary>
    private InstrumentSizeMetadata? _metadata;

    /// <summary>Whether the risk-percent text saw a user edit — a stored default never overwrites user intent.</summary>
    private bool _riskPercentTouched;

    #endregion Private Fields

    #region Properties

    private bool _isRowVisible;
    private bool _isRiskModeAvailable;
    private string _riskPercentText = string.Empty;
    private OrderSide _requestedSide;
    private decimal? _ticketEntryPrice;
    private decimal? _ticketStopDistance;
    private Price? _referencePrice;
    private SizingBalanceSnapshot? _balanceSnapshot;
    private string? _snapshotDetail;
    private bool _isSnapshotAvailable;
    private string? _proposalState;

    /// <summary>The capture time and served amounts of the current snapshot, displayed plainly.</summary>
    public string? SnapshotDetail
    {
        get => _snapshotDetail;
        private set => this.RaiseAndSetIfChanged(ref _snapshotDetail, value);
    }

    /// <summary>True once a captured balance snapshot exists for the committed symbol.</summary>
    public bool IsSnapshotAvailable
    {
        get => _isSnapshotAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isSnapshotAvailable, value);
    }

    /// <summary>True while no snapshot exists: the row says the reference is absent (a failed capture is absence, never zeros).</summary>
    public bool IsSnapshotMissing => !IsSnapshotAvailable;

    /// <summary>A state note about the last proposal attempt (a balance flooring below one step); null when quiet.</summary>
    public string? ProposalState
    {
        get => _proposalState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _proposalState, value);
            this.RaisePropertyChanged(nameof(HasProposalState));
        }
    }

    /// <summary>True while <see cref="ProposalState"/> holds text; drives the state line visibility.</summary>
    public bool HasProposalState => _proposalState is not null;

    /// <summary>
    /// Risk-percent text, parsed with the invariant culture (a percent of
    /// the captured quote balance, 1 == 1 %). A write marks the field
    /// user-owned: stored defaults never overwrite the user's own intent.
    /// </summary>
    public string RiskPercentText
    {
        get => _riskPercentText;
        set
        {
            _riskPercentTouched = true;
            this.RaiseAndSetIfChanged(ref _riskPercentText, value);
        }
    }

    /// <summary>The order side the ticket currently holds: a sell spends base custody, a buy quote.</summary>
    public OrderSide RequestedSide
    {
        get => _requestedSide;
        private set => this.RaiseAndSetIfChanged(ref _requestedSide, value);
    }

    /// <summary>
    /// The ticket's own execution price when the order type carries one
    /// (limit, stop-limit, the OCO take-profit leg); null otherwise.
    /// </summary>
    public decimal? TicketEntryPrice
    {
        get => _ticketEntryPrice;
        private set => this.RaiseAndSetIfChanged(ref _ticketEntryPrice, value);
    }

    /// <summary>
    /// The captured market reference close of the committed symbol: the
    /// buy-side entry estimate for order types without an explicit price
    /// (market, trailing stop).
    /// </summary>
    public Price? ReferencePrice
    {
        get => _referencePrice;
        private set => this.RaiseAndSetIfChanged(ref _referencePrice, value);
    }

    /// <summary>The entry price a buy-side preset divides by: the ticket's own price when carried, the captured reference otherwise.</summary>
    public decimal? EffectiveEntryPrice => _ticketEntryPrice ?? _referencePrice?.Value;

    /// <summary>The captured balance moment the presets and risk proposals ride; null until a capture lands.</summary>
    public SizingBalanceSnapshot? BalanceSnapshot
    {
        get => _balanceSnapshot;
        private set
        {
            this.RaiseAndSetIfChanged(ref _balanceSnapshot, value);
            ComposeSnapshotDetail();
        }
    }

    /// <summary>The ticket's stop distance (the per-unit money at risk) when the order type anchors one; null otherwise.</summary>
    public decimal? TicketStopDistance
    {
        get => _ticketStopDistance;
        private set => this.RaiseAndSetIfChanged(ref _ticketStopDistance, value);
    }

    /// <summary>The risk-percent mode: visible only while the order shape carries a stop distance AND the row is visible.</summary>
    public bool IsRiskModeAvailable
    {
        get => _isRiskModeAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isRiskModeAvailable, value);
    }

    /// <summary>Whether the row applies at all: metadata-known pair instruments only.</summary>
    public bool IsRowVisible
    {
        get => _isRowVisible;
        private set => this.RaiseAndSetIfChanged(ref _isRowVisible, value);
    }

    /// <summary>Quantity texts proposed by the presets; the ticket routes them into its own quantity field.</summary>
    public IObservable<string> QuantityProposals => _quantityProposals;

    /// <summary>Applies the 25 % balance preset.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyQuarterCommand { get; }

    /// <summary>Applies the 50 % balance preset.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyHalfCommand { get; }

    /// <summary>Applies the 100 % balance preset.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyWholeCommand { get; }

    /// <summary>Applies the risk-percent proposal from the ticket's stop distance.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyRiskCommand { get; }

    /// <summary>
    /// Deliberately re-captures the balance and reference snapshots for
    /// the committed symbol — the freshness lever. It is user-invoked only:
    /// captures happen here, and every sizing click after one rides the new
    /// moment, with zero network activity inside the sizing itself. It stays
    /// usable while the row is visible even when the last capture failed —
    /// an absent reference is recoverable by refresh, never stuck.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshSnapshotCommand { get; }

    /// <summary>
    /// The capture-publish stream: one emission per capture leg that
    /// actually published a context. The view does not bind it — it is the
    /// deterministic seam tests await; production logging rides the same
    /// pipeline.
    /// </summary>
    public IObservable<RxVoid> ContextCaptures => _capturesPublished;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the row: wire the capture pipeline (a symbol commit replaces
    /// the in-flight capture — the latest context wins, a superseded fetch
    /// never publishes), the reactive settings defaults (T-053 canon) and
    /// the preset commands gated on the current snapshot and order shape.
    /// </summary>
    public OrderSizerRowViewModel(
        PositionSizingService sizing,
        ISettingsService settings,
        ILocalizationManager localization,
        ILogger<OrderSizerRowViewModel> logger)
    {
        _sizing = sizing ?? throw new ArgumentNullException(nameof(sizing));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var presetsCanExecute = this.WhenAnyValue(
            x => x.BalanceSnapshot,
            x => x.RequestedSide,
            x => x.TicketEntryPrice,
            x => x.ReferencePrice,
            (snapshot, side, ticketEntry, reference) =>
                CanProposePresets(snapshot, side, ticketEntry ?? reference?.Value));

        var riskCanExecute = this.WhenAnyValue(
            x => x.BalanceSnapshot,
            x => x.TicketStopDistance,
            x => x.RiskPercentText,
            (snapshot, stopDistance, percentText) =>
                CanProposeRisk(snapshot, stopDistance, percentText));

        var refreshCanExecute = this
            .WhenAnyValue(x => x.IsRowVisible)
            .Select(static visible => visible);

        ApplyQuarterCommand = ReactiveCommand.Create(() => ProposePreset(QuarterFraction), presetsCanExecute);
        ApplyHalfCommand = ReactiveCommand.Create(() => ProposePreset(HalfFraction), presetsCanExecute);
        ApplyWholeCommand = ReactiveCommand.Create(() => ProposePreset(WholeFraction), presetsCanExecute);
        ApplyRiskCommand = ReactiveCommand.Create(ProposeRisk, riskCanExecute);
        RefreshSnapshotCommand = ReactiveCommand.CreateFromTask(HandleRefreshSnapshotAsync, refreshCanExecute);

        // The capture pipeline: every commit REPLACES the running capture
        // (Switch), so a later context is never answered by an earlier
        // fetch and a superseded context never publishes anything.
        _disposables.Add(
            _symbolCommitted
                .Select(StartCapture)
                .Switch()
                .Subscribe(
                    _ => _capturesPublished.OnNext(RxVoid.Default),
                    ex => _logger.LogError(ex, "Position sizer: the snapshot capture pipeline stopped unexpectedly")));

        // Reactive settings: the stored default re-applies live until the
        // user owns the field.
        _disposables.Add(
            settings.Changes.Subscribe(
                OnSettingChanged,
                ex => _logger.LogError(ex, "Position sizer: the settings change stream stopped unexpectedly")));

        _disposables.Add(_symbolCommitted);
        _disposables.Add(_quantityProposals);
        _disposables.Add(_capturesPublished);
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Load the stored default risk percent into the row's untouched
    /// field. Called once per ticket open (the ticket's initialize
    /// command); a hot settings change rides the reactive subscription
    /// afterwards.
    /// </summary>
    /// <param name="ct">The ticket open's token.</param>
    public async Task InitializeDefaultsAsync(CancellationToken ct = default)
    {
        var percent = await _settings.GetAsync<double>(
            SizerSettingsKeys.Section,
            SizerSettingsKeys.DefaultRiskPercent,
            SizerSettingsKeys.DefaultRiskPercentValue);
        ApplyDefaultRiskPercent(percent);
    }

    /// <summary>
    /// The ticket pushed a symbol: a metadata-known pair commits a new
    /// capture context; anything without metadata (stocks, unknown
    /// instruments, unparsable text) hides the row and holds NO stale
    /// context from a previous instrument.
    /// </summary>
    public void UpdateSymbol(Symbol? symbol)
    {
        var metadata = ResolveMetadata(symbol);
        if (metadata is null)
        {
            if (!IsRowVisible)
            {
                // Already hidden — per-keystroke unparsable text churns
                // nothing and drop nothing.
                return;
            }

            ResetSymbolContext();
            return;
        }

        IsRowVisible = true;
        if (_committedSymbol == symbol)
        {
            return;
        }

        _committedSymbol = symbol;
        _metadata = metadata;

        // A new instrument starts with NO context: the previous
        // instrument's snapshot and reference are never carried over.
        BalanceSnapshot = null;
        ReferencePrice = null;
        ProposalState = null;

        // The metadata answer proves a parsed pair: the commit is safe.
        _symbolCommitted.OnNext(symbol!);
    }

    /// <summary>
    /// The ticket pushed its current order shape: the side, the type's
    /// execution price when it carries one, and the type's stop distance
    /// when it anchors one. The ticket decides the semantics — the row
    /// only reflects them.
    /// </summary>
    public void UpdateOrderShape(
        OrderSide side,
        OrderType? type,
        decimal? ticketEntryPrice,
        decimal? ticketStopDistance)
    {
        RequestedSide = side;
        TicketEntryPrice = ticketEntryPrice;
        TicketStopDistance = ticketStopDistance;
        IsRiskModeAvailable = IsRowVisible && ticketStopDistance is not null;
        ProposalState = null;
    }

    /// <summary>
    /// Release the pipeline subscriptions. The owning ticket disposes the
    /// row with itself — idempotent, the composite guards repeated calls.
    /// </summary>
    public void Dispose() => _disposables.Dispose();

    #endregion Public Methods

    #region Private Methods

    /// <summary>Metadata for a symbol the row can apply to: a metadata-known pair instrument only.</summary>
    private InstrumentSizeMetadata? ResolveMetadata(Symbol? symbol) =>
        symbol is null || symbol.QuoteAsset is null
            ? null
            : _sizing.FindMetadata(symbol);

    /// <summary>Hide the row and drop every captured context (a hidden row owns nothing).</summary>
    private void ResetSymbolContext()
    {
        IsRowVisible = false;
        IsRiskModeAvailable = false;
        _committedSymbol = null;
        _metadata = null;
        BalanceSnapshot = null;
        ReferencePrice = null;
        TicketEntryPrice = null;
        TicketStopDistance = null;
        ProposalState = null;
    }

    /// <summary>
    /// The observable capture leg of one symbol commit; the pipeline's
    /// <c>Switch</c> cancels a superseded leg, so a fetch that lost its
    /// commit is abandoned rather than published.
    /// </summary>
    private IObservable<RxVoid> StartCapture(Symbol symbol) =>
        Observable
            .FromAsync(ct => CaptureSymbolContextAsync(symbol, ct))
            .Select(static _ => RxVoid.Default);

    /// <summary>
    /// Catch both sizing facts for the committed symbol (the balance then
    /// the reference close) and publish. The publish is guarded to the
    /// still-committed symbol, belt under the Switch: an instrument that
    /// lost its commit never gets its data shown.
    /// </summary>
    private async Task CaptureSymbolContextAsync(Symbol symbol, CancellationToken ct)
    {
        var balance = await _sizing.CaptureBalanceSnapshotAsync(symbol, ct);
        var reference = await _sizing.CaptureReferencePriceAsync(symbol, ct);

        if (_committedSymbol != symbol)
        {
            // A later commit replaced this capture's instrument: the
            // results belong to an instrument the ticket no longer holds.
            // Dropped, never applied over the newer context.
            return;
        }

        BalanceSnapshot = balance;
        ReferencePrice = reference;
    }

    /// <summary>The deliberate refresh: a fresh capture for the still-committed symbol.</summary>
    private async Task HandleRefreshSnapshotAsync(CancellationToken ct)
    {
        var symbol = _committedSymbol;
        if (symbol is null)
        {
            return;
        }

        await CaptureSymbolContextAsync(symbol, ct);
    }

    /// <summary>One preset click: compute, floor onto the instrument's step, propose into the ticket.</summary>
    private void ProposePreset(decimal fraction)
    {
        var snapshot = _balanceSnapshot;
        var step = _metadata?.QuantityStep;
        if (snapshot is null
            || step is null
            || !CanProposePresets(snapshot, _requestedSide, EffectiveEntryPrice))
        {
            return;
        }

        try
        {
            var raw = _requestedSide == OrderSide.Sell
                ? PositionSizingMath.BalanceSellQuantity(snapshot.BaseAvailable!.Value, fraction)
                : PositionSizingMath.BalanceBuyQuantity(
                    snapshot.QuoteAvailable!.Value,
                    fraction,
                    EffectiveEntryPrice!.Value);
            Propose(raw, step.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // The can-execute gates run on the UI thread's raised state;
            // the math re-validates. A drift lands logged, never thrown.
            _logger.LogWarning(
                ex,
                "Position sizer: the preset inputs were underdetermined at compute time");
        }
    }

    /// <summary>The risk-percent click: the budget across the ticket's stop distance, floored, proposed.</summary>
    private void ProposeRisk()
    {
        var snapshot = _balanceSnapshot;
        var step = _metadata?.QuantityStep;
        var stopDistance = _ticketStopDistance;
        var percent = TryParseRiskPercent(_riskPercentText);
        if (snapshot is null
            || step is null
            || stopDistance is null
            || percent is null
            || !CanProposeRisk(snapshot, stopDistance, _riskPercentText))
        {
            return;
        }

        try
        {
            var raw = PositionSizingMath.RiskQuantity(
                snapshot.QuoteAvailable!.Value,
                percent.Value,
                stopDistance.Value);
            Propose(raw, step.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(
                ex,
                "Position sizer: the risk inputs were underdetermined at compute time");
        }
    }

    /// <summary>
    /// Floor the raw proposal and route it. A zero floor means the
    /// custody balance sits below one lot step: proposing zero would be a
    /// plausible-but-wrong read (zero is not a size), so the row says so
    /// and writes nothing.
    /// </summary>
    private void Propose(decimal rawQuantity, decimal quantityStep)
    {
        var quantity = PositionSizingMath.FloorToStep(rawQuantity, quantityStep);
        if (quantity <= 0m)
        {
            ProposalState = _localization.GetString(NoProposalKey);
            return;
        }

        _quantityProposals.OnNext(quantity.ToString(CultureInfo.InvariantCulture));
        ProposalState = null;
    }

    /// <summary>Apply the stored default risk percent: only onto the untouched field, only positive values.</summary>
    private void ApplyDefaultRiskPercent(double percent)
    {
        if (_riskPercentTouched || percent <= 0)
        {
            return;
        }

        _riskPercentText = percent.ToString(CultureInfo.InvariantCulture);
        this.RaisePropertyChanged(nameof(RiskPercentText));
    }

    /// <summary>A sizing default change re-applies onto the untouched field (hot settings, T-053 canon).</summary>
    private void OnSettingChanged(SettingsChange change)
    {
        if (change.Section != SizerSettingsKeys.Section
            || change.Key != SizerSettingsKeys.DefaultRiskPercent
            || change.NewValue is not double percent)
        {
            return;
        }

        ApplyDefaultRiskPercent(percent);
    }

    /// <summary>Rebuild the snapshot's plain display line: the captured amounts and their capture time.</summary>
    private void ComposeSnapshotDetail()
    {
        var snapshot = _balanceSnapshot;
        if (snapshot is null)
        {
            SnapshotDetail = null;
            IsSnapshotAvailable = false;
            return;
        }

        SnapshotDetail = _localization.GetString(
            SnapshotDetailKey,
            FormatAsset(snapshot.QuoteAvailable, snapshot.QuoteAsset),
            FormatAsset(snapshot.BaseAvailable, snapshot.BaseAsset),
            snapshot.CapturedAt.ToString(TimeFormats.SizeSnapshotTime, CultureInfo.InvariantCulture));
        IsSnapshotAvailable = true;
    }

    /// <summary>One asset's plain display: the captured amount with its code, or the explicit missing marker.</summary>
    private string FormatAsset(decimal? amount, string asset) =>
        amount is { } held
            ? $"{held.ToString(CultureInfo.InvariantCulture)} {asset}"
            : _localization.GetString(SnapshotAssetMissingKey);

    /// <summary>
    /// Guard the presets: a present snapshot, side-served custody and — on
    /// the buy side — a positive entry estimate for the conversion.
    /// </summary>
    private static bool CanProposePresets(
        SizingBalanceSnapshot? snapshot,
        OrderSide side,
        decimal? entryEstimate) => snapshot is not null && side switch
    {
        OrderSide.Sell => snapshot.BaseAvailable is > 0m,
        OrderSide.Buy => snapshot.QuoteAvailable is > 0m && entryEstimate is > 0m,
        _ => false,
    };

    /// <summary>Guard the risk mode: quote custody, a positive stop distance and a positive parsed percent.</summary>
    private static bool CanProposeRisk(
        SizingBalanceSnapshot? snapshot,
        decimal? stopDistance,
        string percentText) =>
        snapshot?.QuoteAvailable is > 0m
        && stopDistance is > 0m
        && TryParseRiskPercent(percentText) is > 0;

    /// <summary>
    /// Parse the risk-percent text with the invariant culture: empty,
    /// garbage and non-positive are an absent value, never a substituted
    /// default.
    /// </summary>
    private static decimal? TryParseRiskPercent(string raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent)
            && percent > 0m
            ? percent
            : null;

    #endregion Private Methods
}
