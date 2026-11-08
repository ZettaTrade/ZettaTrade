namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
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
/// </remarks>
public sealed class SignalRailViewModel : ReactiveObject, ISignalRailPrefillSink
{
    #region Constants

    /// <summary>The bounded row count the panel ships with (the "N-item" rail): enough to read a busy burst's tail, small enough that a full re-render stays trivially cheap.</summary>
    public const int MaxRows = 12;

    /// <summary>The coalesce window: signals arriving within one window land as ONE rows replacement.</summary>
    public static readonly TimeSpan RailCoalesceWindow = TimeSpan.FromMilliseconds(250);

    #endregion Constants

    #region Private Fields

    private readonly ILogger<SignalRailViewModel> _logger;
    private readonly ILocalizationManager _localization;

    /// <summary>Row presses raised to the window's route (the sink's stream).</summary>
    private readonly Subject<SignalRailRow> _rowPrefills = new();

    /// <summary>Subscription of the coalesced streaming chain; the app lifetime outlives it.</summary>
    private readonly IDisposable _streamSubscription;

    private IReadOnlyList<SignalRailRow> _rows = [];

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
    /// <param name="logger">The rail's logger.</param>
    public SignalRailViewModel(
        IObservable<StrategySignal> signals,
        ILocalizationManager localization,
        ILogger<SignalRailViewModel> logger)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _streamSubscription = (signals ?? throw new ArgumentNullException(nameof(signals)))
            .Where(signal => signal.Stance != StrategyStance.Neutral)
            .Buffer(RailCoalesceWindow)
            .Where(static batch => batch.Count > 0)
            .ObserveOnUiThread()
            .Subscribe(
                ApplyBatch,
                error => _logger.LogError(error, "The signal rail stream stopped unexpectedly"));
    }

    #endregion Constructors

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
        var rebuilt = new List<SignalRailRow>(MaxRows);
        for (var index = batch.Count - 1; index >= 0; index--)
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
