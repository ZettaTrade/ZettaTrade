namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The update checker (T-213): on a bounded cadence (and on demand from
/// the Help menu) it asks the GitHub Releases API for this repository's
/// published releases, ranks the parsable tags against the running
/// version with the pre-release-aware comparison, and answers ONE
/// question on its result stream: is there a newer release, and where
/// does it read? A newer release surfaces twice — the status-bar chip
/// (the stream the status bar subscribes) and ONE notification whose
/// click opens the release page. Nothing downloads, nothing installs:
/// the alpha posture is awareness only, the human decides.
/// <para>
/// Offline-honest: an unreachable endpoint (any transport, HTTP or
/// parse fault) degrades to a logged warning — the standing verdict on
/// the stream stays exactly as it was, the chip never flaps on a
/// failed check. The no-release 404 is the opposite shape: the private
/// feed without a published release answers the honest empty verdict
/// (the quiet no-update state), never an error family (T-230). The
/// <c>App.UpdateCheck</c> off switch silences the
/// check entirely: no request, no chip, no notification; the periodic
/// loop keeps ticking but skips its work while the switch is off, and
/// the manual check no-ops the same way.
/// </para>
/// </summary>
/// <remarks>
/// The first check rides its own boot task (started by
/// <see cref="InitializeAsync"/>, never awaited by the walk — the cold
/// start stays bounded), then the cadence takes over. Announcements
/// dedupe per discovered version for the whole process lifetime: a
/// repeated check (the cadence, or the user pressing the menu item
/// again) never re-toasts the same release. Every completed check
/// emits its verdict on the result stream; a failed check emits
/// nothing.
/// </remarks>
public sealed class UpdateCheckService(
    ISettingsService settings,
    IHttpClientFactory httpClientFactory,
    NotificationCenter notifications,
    ILogger<UpdateCheckService> logger,
    string? currentVersion = null) : IAsyncInitializable, IAsyncDisposable
{
    #region Constants

    /// <summary>The releases endpoint the check reads (this repository's published releases).</summary>
    public const string ReleasesUrl = "https://api.github.com/repos/zettatrade/zettatrade/releases";

    /// <summary>The named HttpClient the check rides (registered by <c>AddUpdateServices</c>).</summary>
    public const string HttpClientName = "GitHubReleases";

    /// <summary>
    /// The check cadence: the request bound (a running session asks at
    /// most this often; the manual check rides the same singleflight).
    /// Pinned public: the cadence is a test-visible contract, not an
    /// implementation detail.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    #endregion Constants

    #region Private Fields

    /// <summary>The settings service the off switch rides (the reactive canon: seed + live changes).</summary>
    private readonly ISettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>The HTTP client factory the named client resolves through.</summary>
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory
        ?? throw new ArgumentNullException(nameof(httpClientFactory));

    /// <summary>The notification center the update announcement publishes through.</summary>
    private readonly NotificationCenter _notifications = notifications
        ?? throw new ArgumentNullException(nameof(notifications));

    /// <summary>The checker's own logger.</summary>
    private readonly ILogger<UpdateCheckService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The running version the remote tags rank against (the exe's
    /// informational spelling; the test constructor pins its own shape).
    /// </summary>
    private readonly string _currentVersion = currentVersion ?? AppVersion.Current;

    /// <summary>The verdict stream: the standing newest release (or none), replayed on every subscription.</summary>
    private readonly BehaviorSubject<UpdateRelease?> _results = new(null);

    /// <summary>Cancellation of the loop at disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>The loop task, tracked and awaited at disposal (never abandoned).</summary>
    private Task? _checkLoop;

    /// <summary>The boot check's task, tracked and awaited at disposal (the loop carries only the cadence).</summary>
    private Task? _firstCheck;

    /// <summary>The off-switch cache: the seed read fills it, the change stream keeps it current.</summary>
    private volatile bool _enabled = AppSettingsKeys.DefaultUpdateCheckEnabled;

    /// <summary>The version already announced this session (the re-toast dedupe); null until one is.</summary>
    private string? _announcedVersion;

    /// <summary>The standing verdict's release (null until a completed check found one newer).</summary>
    private UpdateRelease? _latest;

    /// <summary>The singleflight guard: one check at a time, whichever ask arrives first wins.</summary>
    private int _checking;

    /// <summary>The off-switch subscription, disposed at disposal (the test lifetimes exercise it).</summary>
    private IDisposable? _changesSubscription;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The verdict stream: after every COMPLETED check it carries the
    /// newest release found above the running version, or null when the
    /// endpoint offers nothing newer (the chip reads both states). A
    /// failed check emits nothing — the standing verdict stays.
    /// Subscriptions replay the current verdict (the status bar may
    /// attach after the first check already completed).
    /// </summary>
    public IObservable<UpdateRelease?> Results => _results;

    /// <summary>The standing newest release (the last completed check's verdict); null before the first one.</summary>
    public UpdateRelease? Latest => _latest;

    /// <summary>Whether the off switch currently allows the check (the seed, kept live by the change stream).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// The boot check's task (the test seam): awaiting it settles the
    /// first check's completion, so the battery never races the loop's
    /// own start. Null before <see cref="InitializeAsync"/> runs.
    /// </summary>
    internal Task? FirstCheckForTests => _firstCheck;

    #endregion Properties

    #region Lifecycle

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // The seed read settles INLINE (the reactive settings canon): the
        // loop's first check must see the stored switch, never the default
        // racing it — the walk awaits this initialization, and the read is
        // the same in-memory/SQLite cost every other seed carries.
        try
        {
            _enabled = await _settings.GetAsync<bool>(
                AppSettingsKeys.Section,
                AppSettingsKeys.UpdateCheck,
                AppSettingsKeys.DefaultUpdateCheckEnabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the update check enable state");
        }

        // The change stream keeps the switch current without a restart:
        // an external edit (or the settings window's save) re-applies live.
        _changesSubscription = _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.UpdateCheck)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _enabled = enabled,
                ex => _logger.LogError(ex, "The update check enable stream stopped unexpectedly"));

        // The first check rides its own background task (the walk never
        // awaits the network), then the cadence loop takes over. The
        // separate task also gives the test battery a deterministic
        // join: awaiting FirstCheckForTests settles the boot check.
        _firstCheck = Task.Run(
            () => CheckOnceGuardedAsync(_lifetimeCts.Token),
            CancellationToken.None);
        _checkLoop = RunCheckLoopAsync(_lifetimeCts.Token);
    }

    /// <summary>Stop the loop.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();

        foreach (var task in new[] { _firstCheck, _checkLoop })
        {
            if (task is not { } running)
            {
                continue;
            }

            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                // The tasks are expected to die through cancellation.
            }
        }

        _changesSubscription?.Dispose();
        _results.Dispose();
        _lifetimeCts.Dispose();
    }

    #endregion Lifecycle

    #region Public Methods

    /// <summary>
    /// Run one check now (the Help menu's manual path). The off switch
    /// gates it exactly like the cadence: a disabled check no-ops with
    /// a logged note, never a request. A check already in flight makes
    /// this one a no-op too (the singleflight guard — whichever ask
    /// arrived first carries the answer).
    /// </summary>
    /// <param name="ct">Cooperative cancellation of the request.</param>
    /// <returns>A task completing when the check finished (or was refused).</returns>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (!_enabled)
        {
            _logger.LogDebug("The manual update check is off: the App.UpdateCheck switch silences it");
            return;
        }

        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            _logger.LogDebug("An update check is already in flight; the manual ask rides it");
            return;
        }

        try
        {
            await CheckOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The cadence loop: every tick re-evaluates the off switch before
    /// asking. (The boot check rides its own task — see InitializeAsync.)
    /// One failed pass never kills the loop — the next tick retries.
    /// </summary>
    private async Task RunCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckOnceGuardedAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the loop is expected to die here.
        }
    }

    /// <summary>One cadence tick: gate on the off switch (a silenced tick is a silent no-op) and run guarded.</summary>
    private async Task CheckOnceGuardedAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await CheckOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// One check: fetch the releases list, rank the parsable tags
    /// against the running version, emit the verdict and announce a
    /// NEW release exactly once. Any fault (transport, HTTP status,
    /// parse) degrades to a logged warning — the standing verdict on
    /// the stream stays, nothing emits. The no-release 404 is not a
    /// fault: the feed without a published release is the honest empty
    /// answer, the quiet no-update state (T-230).
    /// </summary>
    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
            UpdateRelease? newest = null;
            UpdateVersion newestVersion = default;
            foreach (var release in releases)
            {
                if (UpdateVersionComparer.Parse(release.Version) is not { } candidate)
                {
                    continue;
                }

                if (newest is null || candidate.CompareTo(newestVersion) > 0)
                {
                    newest = release;
                    newestVersion = candidate;
                }
            }

            if (newest is not null && UpdateVersionComparer.IsNewer(_currentVersion, newest.Version))
            {
                _latest = newest;
                _results.OnNext(newest);
                AnnounceOnce(newest);
            }
            else
            {
                // Nothing newer (or nothing parsable at all): the honest
                // empty verdict clears the standing chip.
                _latest = null;
                _results.OnNext(null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_lifetimeCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "The update check failed; the standing verdict stays until a completed check replaces it");
        }
    }

    /// <summary>
    /// Fetch the releases list and shape it into the ranked candidates
    /// (drafts and unparsable tags skipped). A no-release 404 is the
    /// honest no-update answer of a feed without published releases and
    /// returns the empty list — never a failure (T-230); every other
    /// non-success status keeps its throw.
    /// </summary>
    private async Task<UpdateRelease[]> FetchReleasesAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(new Uri(ReleasesUrl), HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            // The feed's no-release answer (T-230): a repository with no
            // published releases 404s every check — the honest empty
            // verdict (the quiet no-update state) is the answer, and the
            // debug note is the only trace. A real failure (rate limit,
            // server fault, network) keeps its warning lane.
            _logger.LogDebug("The releases feed answers no published releases: the honest no-update state");
            return [];
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException("The releases endpoint answered with a non-array payload.");
        }

        var releases = new List<UpdateRelease>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            // Draft releases are not published answers — a draft never
            // ranks, exactly like an unparsable tag (both skipped).
            if (element.TryGetProperty("draft", out var draft) && draft.ValueKind is JsonValueKind.True)
            {
                continue;
            }

            if (!element.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind is not JsonValueKind.String
                || UpdateVersionComparer.Parse(tag.GetString()) is not { } parsed)
            {
                continue;
            }

            if (!element.TryGetProperty("html_url", out var url)
                || url.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            releases.Add(new UpdateRelease(parsed.ToDisplayString(), url.GetString()!));
        }

        return [.. releases];
    }

    /// <summary>Announce one NEW release: exactly one notification per discovered version per process.</summary>
    private void AnnounceOnce(UpdateRelease release)
    {
        if (string.Equals(_announcedVersion, release.Version, StringComparison.Ordinal))
        {
            return;
        }

        _announcedVersion = release.Version;
        _notifications.Publish(Notification.ForUpdateAvailable(
            release.Version,
            release.Url,
            DateTimeOffset.UtcNow));
        _logger.LogInformation(
            "Update available: {Version} ({Url})",
            release.Version,
            release.Url);
    }

    #endregion Private Methods
}
