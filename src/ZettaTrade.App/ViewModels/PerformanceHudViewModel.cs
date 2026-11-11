namespace ZettaTrade.App.ViewModels;

using System;
using System.Globalization;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The performance HUD overlay's ViewModel (T-108): a dev-mode readout of
/// the chart's frame rate, the book stream's rate and latency, the working
/// memory and the log tail's backpressure gauge. The visibility is a
/// persisted <c>Diagnostics.HudEnabled</c> choice (off by default), the
/// same cache-leads persistence discipline the AI panel's visibility keeps;
/// the sampler runs only while the overlay shows, so the off state leaves
/// zero traces. The lines re-format at the sampler's own 1 Hz cadence — a
/// language switch lands with the next tick, no extra subscription.
/// </summary>
public sealed class PerformanceHudViewModel : ReactiveObject, IAsyncInitializable
{
    /// <summary>The sampler's readout cadence (the lines re-format per tick).</summary>
    private const int GaugeSegments = 8;

    /// <summary>The histogram line's sample separator (a mid dot reads cleanly over a dark overlay).</summary>
    private const string BucketSeparator = " · ";

    private readonly ISettingsService _settings;
    private readonly ILocalizationManager _localization;
    private readonly PerformanceHudService _service;
    private readonly ILogger<PerformanceHudViewModel> _logger;

    private bool _persistedHudEnabled = UiSettingsKeys.DefaultHudEnabled;

    private bool _isVisible = UiSettingsKeys.DefaultHudEnabled;

    private string[] _lines = [];

    /// <summary>The snapshots leg armed while the overlay shows; null (and silent) off.</summary>
    private IDisposable? _snapshotsLeg;

    /// <summary>
    /// Create a new PerformanceHudViewModel instance.
    /// </summary>
    /// <param name="settings">The settings service (the persistence pipeline and the live changes).</param>
    /// <param name="localization">The localization manager (the line labels).</param>
    /// <param name="service">The HUD sampler the visibility drives.</param>
    /// <param name="logger">The ViewModel logger.</param>
    public PerformanceHudViewModel(
        ISettingsService settings,
        ILocalizationManager localization,
        PerformanceHudService service,
        ILogger<PerformanceHudViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The sampler follows the visibility: shown — started (and the
        // snapshots leg re-formats the lines per tick, on the UI thread);
        // hidden — every leg dies (the subscription first, then the
        // sampler's own composite), so the off state leaves zero traces.
        // The when-any-value stream emits the current (default off) value
        // at once — a stop over an off sampler is the service's no-op.
        this.WhenAnyValue(x => x.IsVisible)
            .Subscribe(
                visible =>
                {
                    if (visible)
                    {
                        _service.Start();
                        _snapshotsLeg = _service.Snapshots
                            .ObserveOnUiThread()
                            .Subscribe(
                                ApplySnapshot,
                                ex => _logger.LogError(ex, "The performance HUD's snapshot stream stopped unexpectedly"));
                    }
                    else
                    {
                        _snapshotsLeg?.Dispose();
                        _snapshotsLeg = null;
                        _service.Stop();
                    }
                },
                ex => _logger.LogError(ex, "The performance HUD visibility stream stopped unexpectedly"));

        // Every flip persists like the AI panel's visibility does: the cache
        // LEADS the pipeline — updated at issue time, in issuance order —
        // while the writes serialize behind it (the BUG-053 discipline), and
        // the real-difference filter keeps the restore's cached seed in
        // charge (reads are never re-written). A failed write is logged and
        // swallowed in the write helper, the pipeline keeps serving flips.
        this.WhenAnyValue(x => x.IsVisible)
            .Skip(1)
            .Where(value => value != _persistedHudEnabled)
            .Do(value => _persistedHudEnabled = value)
            .Select(value => Observable
                .FromAsync(() => SetHudEnabledAsync(value))
                .Select(_ => value))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The performance HUD persistence pipeline stopped unexpectedly"));

        // The visibility follows the settings service live: an external
        // writer applies with the cache leading (so the persistence pipeline
        // above sees an equal value and skips the write). Non-bool values
        // drop — the applied choice stays in charge.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.DiagnosticsSection &&
                             change.Key == UiSettingsKeys.HudEnabled)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                applied =>
                {
                    _persistedHudEnabled = applied;
                    IsVisible = applied;
                },
                ex => _logger.LogError(ex, "The performance HUD visibility changes stream stopped unexpectedly"));

        ToggleCommand = ReactiveCommand.Create<RxVoid>(_ => { IsVisible = !IsVisible; });
    }

    /// <summary>Gets the toggle command (the menu's checkbox item).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    /// <summary>Gets or sets a value indicating whether the overlay shows (the menu checkbox mirrors it OneWay).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    /// <summary>Gets the overlay's readout lines (one per sampler tick; empty until the first tick lands).</summary>
    public string[] Lines
    {
        get => _lines;
        private set => this.RaiseAndSetIfChanged(ref _lines, value);
    }

    /// <summary>
    /// The view glue: attach the named chart's frame-tap drain (the window
    /// owns the control; the sampler drains it per tick). One assignment at
    /// the window's open — never a per-frame cost.
    /// </summary>
    /// <param name="frameDrain">The chart's reset-and-read frame counter.</param>
    public void AttachChartFrames(Func<int> frameDrain) =>
        _service.ChartFrameDrain = frameDrain ?? throw new ArgumentNullException(nameof(frameDrain));

    /// <summary>
    /// Restore the persisted visibility (the startup walk): the cache leads
    /// the assignment, so the restore never rewrites the value it read. The
    /// visibility pipeline starts/stops the sampler per the applied value.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The completed restore.</returns>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var restored = await _settings.GetAsync<bool>(
            UiSettingsKeys.DiagnosticsSection,
            UiSettingsKeys.HudEnabled,
            UiSettingsKeys.DefaultHudEnabled);

        _persistedHudEnabled = restored;
        IsVisible = restored;

        _logger.LogInformation("Restored persisted performance HUD visibility: '{Visibility}'", restored ? "visible" : "hidden");
    }

    /// <summary>
    /// One serialized persistence write for the HUD visibility. A failed
    /// write is logged and swallowed HERE (not at the stream): the pipeline
    /// keeps serving later flips instead of dying on the first fault. The
    /// issued value stays cached after a failure — the file catches up with
    /// the property on the next real difference.
    /// </summary>
    /// <param name="visible">The issued visibility to persist.</param>
    /// <returns>The completed write.</returns>
    private async Task SetHudEnabledAsync(bool visible)
    {
        try
        {
            await _settings.SetAsync(UiSettingsKeys.DiagnosticsSection, UiSettingsKeys.HudEnabled, visible);
        }
        catch (Exception fault)
        {
            _logger.LogError(fault, "The performance HUD visibility write failed; the file catches up on the next flip");
        }
    }

    /// <summary>
    /// Apply one snapshot: re-format the overlay's lines. Runs on the UI
    /// thread (the snapshots leg observes it), once per second while the
    /// overlay shows — off, nothing here ever runs.
    /// </summary>
    private void ApplySnapshot(PerformanceSnapshot snapshot)
    {
        var chart = _localization.GetString(
            "Diagnostics.Hud.Chart",
            snapshot.ChartFps.ToString("F1", CultureInfo.InvariantCulture));
        var book = _localization.GetString("Diagnostics.Hud.Book", snapshot.BookUpdatesPerSecond);
        var memory = _localization.GetString("Diagnostics.Hud.Memory", HudMath.FormatMegabytes(snapshot.WorkingSetBytes));
        var queue = _localization.GetString(
            "Diagnostics.Hud.Queue",
            snapshot.BackpressureBacklog,
            snapshot.BackpressureCapacity,
            HudMath.GaugeBar(
                snapshot.BackpressureCapacity > 0
                    ? (double)snapshot.BackpressureBacklog / snapshot.BackpressureCapacity
                    : 0,
                GaugeSegments));
        var latency = _localization.GetString("Diagnostics.Hud.Latency", FormatBuckets(snapshot));

        Lines = [chart, book, memory, queue, latency];
    }

    /// <summary>
    /// The latency line's histogram body: one "≤Nms count" pair per bounded
    /// bucket and one ">Nms count" for the overflow, joined by mid dots.
    /// The metric is the book's receive-to-render pipeline latency (T-142:
    /// every adapter stamps its books with the local receive moment, so the
    /// ages measure the transport after ingestion, never a clock offset).
    /// The digits and the millisecond unit stay culture-invariant (the
    /// localized label around the body names the metric; the histogram
    /// itself is a data readout).
    /// </summary>
    private static string FormatBuckets(PerformanceSnapshot snapshot)
    {
        var parts = new string[snapshot.LatencyBuckets.Length];
        for (var i = 0; i < snapshot.LatencyBuckets.Length; i++)
        {
            var edge = i < snapshot.LatencyBoundsMs.Length
                ? "≤" + snapshot.LatencyBoundsMs[i].ToString("F0", CultureInfo.InvariantCulture) + "ms"
                : ">" + snapshot.LatencyBoundsMs[^1].ToString("F0", CultureInfo.InvariantCulture) + "ms";
            parts[i] = $"{edge} {snapshot.LatencyBuckets[i]}";
        }

        return string.Join(BucketSeparator, parts);
    }
}
