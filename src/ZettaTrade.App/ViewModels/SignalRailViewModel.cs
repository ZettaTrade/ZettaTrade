namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Livery.Contracts;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Strategies;

/// <summary>
/// The signal rail panel state (T-112): the workspace's newest strategy
/// readings as an N-row bounded list streaming live behind the chart, with
/// the one click affordance the workspace's trading flows keep — a row
/// press PREFILLS the order ticket (T-025's canon: prefill, never submit).
/// Application-lifetime singleton: the streaming pipeline is built in the
/// constructor (WhenActivated never fires for workspace-panel VMs), so
/// the rail streams from the first frame.
/// </summary>
/// <remarks>
/// <para>
/// Burst coalescing (the acceptance bar): the signals stream buffers by
/// a fixed coalesce window and every batch lands as ONE rows replacement
/// — a level burst (a load page's burst of merged strategies) can never
/// produce a row-flip flood, only one settled update per window. The
/// rows list is bounded to <see cref="MaxRows"/>: the oldest rows fall
/// off, the rail never grows with the feed.
/// </para>
/// <para>
/// A neutral stance is an honest non-reading and never enters the rail:
/// every row is a long or flat decision the strategies took, stamped by
/// the host's own bookkeeping. The row press raises its route over
/// <see cref="RowPrefillRequested"/> and the main window lands it on the
/// ticket's prefill path; the rail itself touches no order surface —
/// the human decides.
/// </para>
/// <para>
/// The rail's row height is persisted state (T-177): the startup walk
/// restores <c>UI.SignalRailHeight</c> before any window opens, a settled
/// drag persists once per burst (the debounce keeps a drag from writing
/// the settings file per pixel), the restore and the changes-stream echo
/// never rewrite the value they just applied, and external writers apply
/// live. The height clamps into the usable band both at the splitter (the
/// row's own min/max) and at the stored value (garbage falls back to the
/// shipped default).
/// </para>
/// </remarks>
public sealed class SignalRailViewModel : ReactiveObject, ISignalRailPrefillSink, IAsyncInitializable
{
    #region Constants

    /// <summary>The bounded row count the panel ships with (the "N-item" rail): enough to read a busy burst's tail, small enough that a full re-render stays trivially cheap.</summary>
    public const int MaxRows = 12;

    /// <summary>The coalesce window: signals arriving within one window land as ONE rows replacement.</summary>
    public static readonly TimeSpan RailCoalesceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the persistence waits after the last drag move before it
    /// writes: one write per settled drag burst instead of one per pixel.
    /// </summary>
    private static readonly TimeSpan DragQuietTime = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How much height difference counts as "same" (a float round-trip
    /// through layout rounding never escalates into a settings rewrite).
    /// </summary>
    private const double HeightSameEpsilon = 0.01;

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ILogger<SignalRailViewModel> _logger;
    private readonly ILocalizationManager _localization;

    /// <summary>Row presses raised to the window's route (the sink's stream).</summary>
    private readonly Subject<SignalRailRow> _rowPrefills = new();

    /// <summary>Subscription of the coalesced streaming chain; the app lifetime outlives it.</summary>
    private readonly IDisposable _streamSubscription;

    private IReadOnlyList<SignalRailRow> _rows = [];

    /// <summary>
    /// The rail height the ViewModel last applied. Programmatic writes
    /// (restore, external echoes) update this BEFORE the property changes,
    /// so the persistence pipeline filters them out and only real splitter
    /// drags push differences through.
    /// </summary>
    private double _appliedRailHeight = UiSettingsKeys.DefaultSignalRailHeight;

    private GridLength _railHeight = new(UiSettingsKeys.DefaultSignalRailHeight);

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The rail's rows, newest first, bounded to <see cref="MaxRows"/> and
    /// replaced in one update per coalesce window. Immutable rows — the
    /// list swap is the whole update.
    /// </summary>
    public IReadOnlyList<SignalRailRow> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    /// <summary>Whether the rail carries rows (drives the empty state's visibility).</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>Whether the rail is empty (no reading landed yet, or the row ring cleared).</summary>
    public bool IsEmpty => _rows.Count == 0;

    /// <summary>
    /// The row-press stream the main window subscribes: a press raises its
    /// row here and the window's route turns it into an order-ticket
    /// PREFILL (the single dialog path the book level presses share). The
    /// stream carries the row, never an order.
    /// </summary>
    public IObservable<SignalRailRow> RowPrefillRequested => _rowPrefills;

    /// <summary>
    /// The rail's row height (logical pixels). The chart panel's rail row
    /// binds it TwoWay: the splitter's dragged heights flow in and persist
    /// through the pipeline once the drag settles.
    /// </summary>
    public GridLength RailHeight
    {
        get => _railHeight;
        set => this.RaiseAndSetIfChanged(ref _railHeight, value);
    }

    /// <summary>The row's minimum height clamp the splitter reads (the row's MinHeight binding).</summary>
    public double MinRailHeight { get; } = UiSettingsKeys.MinSignalRailHeight;

    /// <summary>The row's maximum height clamp the splitter reads (the row's MaxHeight binding).</summary>
    public double MaxRailHeight { get; } = UiSettingsKeys.MaxSignalRailHeight;

    #endregion Properties

    #region Constructors

    /// <summary>
    /// Create the rail: subscribe the coalescing pipeline on the host
    /// signal stream (raw to the rail — the pipeline marshals its own
    /// apply), and keep the subscription for the app lifetime (a
    /// workspace singleton mirrors it, no explicit disposal).
    /// </summary>
    /// <param name="signals">The strategy signals stream (the host service's broadcast).</param>
    /// <param name="localization">The localization catalog the row texts compose from.</param>
    /// <param name="settings">The app settings service; the startup walk
    /// restores the rail height from it, a settled drag persists there and
    /// the changes stream carries external writers (T-177).</param>
    /// <param name="logger">The rail's logger.</param>
    public SignalRailViewModel(
        IObservable<StrategySignal> signals,
        ILocalizationManager localization,
        ISettingsService settings,
        ILogger<SignalRailViewModel> logger)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _streamSubscription = (signals ?? throw new ArgumentNullException(nameof(signals)))
            .Where(signal => signal.Stance != StrategyStance.Neutral)
            .Buffer(RailCoalesceWindow)
            .Where(static batch => batch.Count > 0)
            .ObserveOnUiThread()
            .Subscribe(
                ApplyBatch,
                error => _logger.LogError(error, "The signal rail stream stopped unexpectedly"));

        // External writers apply live: the applied height leads the
        // property change, so the persistence pipeline sees an
        // already-applied value and skips it (the workspace-geometry
        // discipline, row-wise).
        settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.SignalRailHeight)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                ApplyExternalRailHeight,
                ex => _logger.LogError(ex, "The signal rail height stream stopped unexpectedly"));

        // Persist every settled drag (the debounce keeps a drag burst from
        // writing the settings file per pixel; Concat serializes the
        // writes). The RailHeight property is the binding writeback target
        // of the rail row, so this pipeline fires exactly on those
        // splitter writes; the restore and the external echo never cross
        // the difference guard.
        this.WhenAnyValue(x => x.RailHeight)
            .Skip(1)
            .Where(length => !IsSameHeight(length, _appliedRailHeight))
            .Throttle(DragQuietTime)
            .Select(length => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.SignalRailHeight,
                    ClampStoredHeight(length.Value)))
                .Select(_ => length))
            .Concat()
            .Subscribe(
                issuedLength => _appliedRailHeight = issuedLength.Value,
                ex => _logger.LogError(ex, "Failed to persist the signal rail height"));
    }

    #endregion Constructors

    #region Height Persistence

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var height = ClampStoredHeight(await _settings.GetAsync<double>(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            UiSettingsKeys.DefaultSignalRailHeight));

        // The applied height leads the property assignment on purpose: a
        // drag the persistence pipeline would observe sits behind the
        // difference guard, so a restore never rewrites the value it just
        // read.
        _appliedRailHeight = height;
        RailHeight = new GridLength(height);

        _logger.LogInformation(
            "Restored persisted signal rail height: {Height:F0}px",
            height);
    }

    /// <summary>
    /// An external writer set <c>UI.SignalRailHeight</c>: the value becomes
    /// the rail's truth (applied live) and the applied lead is set first so
    /// the persistence pipeline stays silent.
    /// </summary>
    private void ApplyExternalRailHeight(double height)
    {
        var applied = ClampStoredHeight(height);
        _appliedRailHeight = applied;
        RailHeight = new GridLength(applied);
    }

    /// <summary>
    /// Clamp a stored height: garbage (non-finite — an out-of-range JSON
    /// magnitude, e.g. <c>1e999</c>, a hand-edited file) falls back to the
    /// shipped default, anything else clamps into the usable band.
    /// </summary>
    private static double ClampStoredHeight(double height) =>
        double.IsFinite(height)
            ? Math.Clamp(height, UiSettingsKeys.MinSignalRailHeight, UiSettingsKeys.MaxSignalRailHeight)
            : UiSettingsKeys.DefaultSignalRailHeight;

    /// <summary>Whether an absolute <see cref="GridLength"/> carries this height (within the rounding epsilon).</summary>
    private static bool IsSameHeight(GridLength length, double height) =>
        length.IsAbsolute && Math.Abs(length.Value - height) < HeightSameEpsilon;

    #endregion Height Persistence

    #region Stream Discipline

    /// <summary>
    /// Land one coalesced batch as ONE rows replacement: the batch's rows
    /// prepend newest-first (a burst's later readings ride above), the
    /// standing rows fill beneath, and the classic push past the capacity
    /// evicts the tail. Rows rebuild per batch — no row ever mutates.
    /// </summary>
    /// <param name="batch">The window's arrived signals (stream order: oldest first).</param>
    private void ApplyBatch(IList<StrategySignal> batch)
    {
        // Empty batch (a window with nothing) never runs: the pipeline's
        // own gate — this body only sees readings.
        // The ring holds at MaxRows across the batch too (T-177's scroll
        // probe caught the gap): a burst wider than the ring keeps its
        // NEWEST MaxRows readings and drops the oldest tail with the
        // standing rows' eviction, so the rail never grows with the feed.
        var rebuilt = new List<SignalRailRow>(MaxRows);
        for (var index = batch.Count - 1; index >= 0 && rebuilt.Count < MaxRows; index--)
        {
            rebuilt.Add(SignalRailRow.For(batch[index], _localization, this));
        }

        foreach (var standing in _rows)
        {
            if (rebuilt.Count >= MaxRows)
            {
                break;
            }

            rebuilt.Add(standing);
        }

        Rows = rebuilt;
        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(IsEmpty));
        _logger.LogDebug(
            "Signal rail coalesced: {Batch} window signals landed as one rows update",
            batch.Count);
    }

    #endregion Stream Discipline

    #region ISignalRailPrefillSink

    /// <inheritdoc/>
    public void RaisePrefill(SignalRailRow row) => _rowPrefills.OnNext(row);

    #endregion ISignalRailPrefillSink
}
