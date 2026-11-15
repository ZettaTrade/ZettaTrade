namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using Livery.Contracts;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Why one staging attempt refused to produce a staged update. Every family
/// is an honest refusal the caller logs and degrades on — the update loop
/// never guesses, never substitutes, never blocks the trading flow.
/// </summary>
public enum UpdateStageFailure
{
    /// <summary>The release lists no archive asset for the running RID.</summary>
    NoAssetForRid,

    /// <summary>The release lists no SHA-256 sums file (the hash cannot be pinned).</summary>
    HashFileMissing,

    /// <summary>The sums file does not list the archive asset's hash.</summary>
    HashNotListed,

    /// <summary>The download's SHA-256 does not match the release's pinned hash.</summary>
    HashMismatch,

    /// <summary>An update URL is not HTTPS (or a redirect left HTTPS).</summary>
    NotHttps,

    /// <summary>The HTTP fetch failed (status or transport).</summary>
    DownloadFault,

    /// <summary>The payload exceeded its size cap.</summary>
    TooLarge,

    /// <summary>The verified archive refused to extract or the extracted build is incomplete.</summary>
    ExtractFault,
}

/// <summary>One staging attempt's honest refusal (the message names the family's facts).</summary>
public sealed class UpdateStagingException : Exception
{
    /// <summary>Create the refusal for one failure family.</summary>
    public UpdateStagingException(UpdateStageFailure failure, string? detail = null)
        : base(detail is null
            ? $"The update staging refused: {failure}."
            : $"The update staging refused: {failure} ({detail}).")
    {
        Failure = failure;
    }

    /// <summary>Why the attempt refused.</summary>
    public UpdateStageFailure Failure { get; }
}

/// <summary>
/// The auto-update's staging service (T-258): from the checker's verdict it
/// carries the release asset for the running RID through the bounded
/// download, the SHA-256 verification against the release's own sums file
/// (the <c>zettatrade-0.2.2-sha256.txt</c> shape, the hash pinned from the
/// release metadata — never from the download response), the staging
/// extraction and the ONE consent question. Nothing applies by itself:
/// consent writes <c>Approved</c>, and the swap happens on the next start
/// (<see cref="UpdateSwapRunner"/>) — a running session is never killed,
/// and the consent prompt discloses the session's open positions and
/// working orders before the user decides (the trading-safety rule).
/// <para>
/// Bounded by design (the T-105/T-106 lessons): the downloads ride one
/// named HttpClient with a hard size cap and a total per-download budget,
/// exactly one staging attempt per version per process runs (no retries —
/// a failure degrades to one logged warning), and the off switch
/// (<c>App.AutoUpdate</c>, default off) silences the whole loop: no
/// download, no staging, no prompt. A failed or declined staging leaves
/// the running build exactly as it was.
/// </para>
/// <para>
/// The verdicts arrive on a worker thread through one unbounded channel
/// with a single sequential reader (the AlertEngine/ReconnectSupervisor
/// pump shape): the network work stays off the UI thread, and the one
/// consent dialog hops onto it exactly for the ask.
/// </para>
/// </summary>
public sealed class UpdateStageService(
    ISettingsService settings,
    IHttpClientFactory httpClientFactory,
    UpdateCheckService checker,
    KillSwitchVenues venues,
    IConfirmationService confirmations,
    ILocalizationManager localization,
    ILogger<UpdateStageService> logger,
    string? installDirectory = null,
    Func<IReadOnlyList<string>>? ridCandidates = null,
    Func<Func<Task<bool>>, Task<bool>>? uiMarshaller = null) : IAsyncInitializable, IAsyncDisposable
{
    #region Constants

    /// <summary>The named HttpClient the downloads ride (registered by <c>AddUpdateServices</c>).</summary>
    public const string HttpClientName = "GitHubReleaseAssets";

    /// <summary>
    /// The download size cap: a release archive of this project is tens of
    /// megabytes; anything larger is not one. The header is checked first,
    /// the streamed byte count second — a lying header never exhausts the disk.
    /// </summary>
    public static readonly long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>The sums file's size cap (a real one is under a kilobyte).</summary>
    public static readonly long MaxHashFileBytes = 64L * 1024;

    /// <summary>
    /// The total download budget: one fetch may use at most this long — a
    /// stalled transfer cannot hang the pump (the bounded-timeout lesson).
    /// </summary>
    public static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(10);

    #endregion Constants

    #region Private Fields

    /// <summary>The settings service the off switch rides (the reactive canon: seed + live changes).</summary>
    private readonly ISettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>The HTTP client factory the named client resolves through.</summary>
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory
        ?? throw new ArgumentNullException(nameof(httpClientFactory));

    /// <summary>The checker whose verdict stream the loop rides.</summary>
    private readonly UpdateCheckService _checker = checker ?? throw new ArgumentNullException(nameof(checker));

    /// <summary>The app's trading venues the consent disclosure reads.</summary>
    private readonly KillSwitchVenues _venues = venues ?? throw new ArgumentNullException(nameof(venues));

    /// <summary>The confirmation service the consent question rides.</summary>
    private readonly IConfirmationService _confirmations = confirmations
        ?? throw new ArgumentNullException(nameof(confirmations));

    /// <summary>The localization manager the disclosure composes through.</summary>
    private readonly ILocalizationManager _localization = localization
        ?? throw new ArgumentNullException(nameof(localization));

    /// <summary>The staging service's own logger.</summary>
    private readonly ILogger<UpdateStageService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>The install directory the staging tree lives in (the test constructor pins its own).</summary>
    private readonly string _installDirectory = installDirectory ?? AppContext.BaseDirectory;

    /// <summary>The platform's RID candidates (the test constructor pins its own).</summary>
    private readonly Func<IReadOnlyList<string>> _ridCandidates = ridCandidates ?? DefaultRidCandidates;

    /// <summary>The UI-thread hop the consent ask rides (the test constructor pins its own inline hop).</summary>
    private readonly Func<Func<Task<bool>>, Task<bool>> _uiMarshaller = uiMarshaller ?? AskOnUi;

    /// <summary>The staging tree's store (manifest + layout).</summary>
    private readonly UpdateStagingStore _store = new(installDirectory ?? AppContext.BaseDirectory);

    /// <summary>The off-switch cache: the seed read fills it, the change stream keeps it current.</summary>
    private volatile bool _autoEnabled;

    /// <summary>The verdict inbox: the checker's verdicts feed ONE sequential pump.</summary>
    private readonly Channel<UpdateRelease> _verdicts = Channel.CreateUnbounded<UpdateRelease>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Cancellation of the pump at disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>The pump task, tracked and awaited at disposal (never abandoned).</summary>
    private Task? _pump;

    /// <summary>The input subscriptions, disposed at disposal (the test lifetimes exercise them).</summary>
    private IDisposable? _subscriptions;

    /// <summary>The versions this process already asked consent for (ONE question per version).</summary>
    private readonly HashSet<string> _askedVersions = new(StringComparer.Ordinal);

    /// <summary>The versions this process's staging already failed once (no retries — a NEW process's check re-offers).</summary>
    private readonly HashSet<string> _failedVersions = new(StringComparer.Ordinal);

    #endregion Private Fields

    #region Lifecycle

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // The seed read settles INLINE (the reactive settings canon): the
        // pump's first verdict must see the stored switch, never the default
        // racing it. The off default means a fresh install downloads nothing.
        try
        {
            _autoEnabled = await _settings.GetAsync<bool>(
                AppSettingsKeys.Section,
                AppSettingsKeys.AutoUpdate,
                AppSettingsKeys.DefaultAutoUpdateEnabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the auto-update enable state");
        }

        // The change stream keeps the switch current without a restart.
        var changes = _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.AutoUpdate)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _autoEnabled = enabled,
                ex => _logger.LogError(ex, "The auto-update switch stream stopped unexpectedly"));

        // The checker's verdicts feed the pump's inbox (the standing verdict
        // replays to a late subscriber — the boot check's answer still drives
        // a staging this session started after it). The stream's null
        // baseline (the checker's standing "no verdict" replay) never enters
        // the inbox: the pump acts on REAL verdicts only.
        var verdicts = _checker.Results
            .Where(verdict => verdict is not null)
            .Subscribe(
                verdict => _verdicts.Writer.TryWrite(verdict!),
                ex => _logger.LogError(ex, "The update verdict stream stopped unexpectedly"));

        _subscriptions = new CompositeDisposable(changes, verdicts);

        // One sequential pump: the staging and the consent question never
        // interleave, and a failed attempt never kills the loop.
        _pump = Task.Run(() => PumpAsync(_lifetimeCts.Token), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Stop the pump.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        _subscriptions?.Dispose();
        _verdicts.Writer.TryComplete();

        if (_pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pump is expected to die through cancellation.
            }
        }

        _lifetimeCts.Dispose();
    }

    #endregion Lifecycle

    #region The pump

    /// <summary>
    /// The one reader: every verdict that arrives while the off switch is on
    /// drives one staging attempt. A fault inside one attempt logs and
    /// degrades — the pump never dies, the trading flow never notices.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var verdict in _verdicts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (verdict is null)
                {
                    // The inbox never carries one (the subscription filters
                    // the stream's null baseline); the pump never trusts it
                    // even if one slipped through.
                    continue;
                }

                if (!_autoEnabled)
                {
                    continue;
                }

                try
                {
                    await HandleVerdictAsync(verdict, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "The staging attempt for {Version} failed; the running build stands",
                        verdict.Version);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the pump is expected to die here.
        }
    }

    /// <summary>
    /// One verdict: stage the release (unless a staged manifest for this
    /// version already stands) and ask the ONE consent question. A version
    /// this process already asked for, or whose staging already failed
    /// here, never repeats — the next process start re-offers both.
    /// </summary>
    private async Task HandleVerdictAsync(UpdateRelease verdict, CancellationToken ct)
    {
        var version = verdict.Version;
        if (string.IsNullOrWhiteSpace(version) || version == AppVersion.Current)
        {
            return;
        }

        if (_askedVersions.Contains(version) || _failedVersions.Contains(version))
        {
            return;
        }

        var manifest = _store.TryRead();
        if (manifest is { State: UpdateSwapState.Approved or UpdateSwapState.Applied })
        {
            // The swap machine owns the lane: a consented (or mid-flight)
            // update applies at the next start, and a newer release
            // re-offers only after that swap settles — a newer verdict
            // never overwrites a standing consent mid-swap.
            return;
        }

        if (manifest is not { State: UpdateSwapState.Staged }
            || !string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            manifest = await StageAsync(verdict, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return;
            }

            SweepSiblingStages(version);
        }

        _askedVersions.Add(version);
        await AskConsentAsync(manifest, ct).ConfigureAwait(false);
    }

    #endregion The pump

    #region The staging

    /// <summary>
    /// Carry the release through the bounded pipeline and write the staged
    /// manifest. Returns the manifest on success, null on any refusal (each
    /// logged once); the staging tree never holds an unverified build.
    /// </summary>
    private async Task<UpdatePendingManifest?> StageAsync(UpdateRelease verdict, CancellationToken ct)
    {
        var version = verdict.Version;
        try
        {
            var assets = verdict.Assets ?? [];
            var archive = UpdateAssetCatalog.PickArchive(assets, version, _ridCandidates())
                ?? throw new UpdateStagingException(UpdateStageFailure.NoAssetForRid, string.Join(", ", _ridCandidates()));
            var hashAsset = UpdateAssetCatalog.PickHashFile(assets, version)
                ?? throw new UpdateStagingException(UpdateStageFailure.HashFileMissing, archive.Name);

            // The HTTPS-only rule checks BOTH the pinned URLs before any
            // byte moves (a redirect that left HTTPS fails the landing
            // check inside the download).
            EnsureHttps(archive.DownloadUrl);
            EnsureHttps(hashAsset.DownloadUrl);

            var versionDirectory = _store.StagedDirectory(version);
            Directory.CreateDirectory(versionDirectory);

            var hashText = await DownloadHashTextAsync(hashAsset, ct).ConfigureAwait(false);
            var expectedHash = UpdateAssetCatalog.ExpectedHashFor(hashText, archive.Name)
                ?? throw new UpdateStagingException(UpdateStageFailure.HashNotListed, archive.Name);

            var archivePath = Path.Combine(versionDirectory, archive.Name);
            await DownloadArchiveAsync(archive, archivePath, ct).ConfigureAwait(false);

            var actualHash = await UpdateAssetCatalog.ComputeSha256HexAsync(archivePath, ct).ConfigureAwait(false);
            if (!UpdateAssetCatalog.HashMatches(expectedHash, actualHash))
            {
                throw new UpdateStagingException(
                    UpdateStageFailure.HashMismatch,
                    $"expected {expectedHash[..8]}, got {actualHash[..8]}");
            }

            ExtractArchive(archivePath, versionDirectory);

            var hostFileName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
            if (hostFileName.Length == 0 || !File.Exists(Path.Combine(versionDirectory, hostFileName)))
            {
                throw new UpdateStagingException(UpdateStageFailure.ExtractFault, hostFileName);
            }

            var manifest = new UpdatePendingManifest(
                version,
                UpdateSwapState.Staged,
                ProofRequired: false,
                archive.Name,
                actualHash,
                DateTimeOffset.UtcNow);
            _store.Write(manifest);
            _logger.LogInformation(
                "The update {Version} staged and verified (SHA-256 {Hash}): the consent question follows",
                version,
                actualHash[..8]);
            return manifest;
        }
        catch (UpdateStagingException ex)
        {
            _failedVersions.Add(version);
            _logger.LogWarning(ex, "The update {Version} did not stage: {Reason}", version, ex.Failure);
            CleanupPartialStaging(version);
            return null;
        }
        catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
        {
            // The download budget cut a stalled transfer: the honest
            // refusal, logged once like every other.
            _failedVersions.Add(version);
            _logger.LogWarning("The update {Version} did not stage: the download exceeded its bounded budget", version);
            CleanupPartialStaging(version);
            return null;
        }
    }

    /// <summary>
    /// Extract one verified archive into its version directory (zip or
    /// tar.gz by the asset's own spelling). The archive itself dies right
    /// after the extraction — BEFORE the layout normalization — because it
    /// extracts into its own directory: a wrapper's one-child detection
    /// must count build files only, never the archive's own shell.
    /// </summary>
    private static void ExtractArchive(string archivePath, string versionDirectory)
    {
        try
        {
            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                TarFile.ExtractToDirectory(archivePath, versionDirectory, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, versionDirectory, overwriteFiles: true);
            }

            // The verified archive's work is done: the extracted build is
            // what the swap moves; the archive's shell dies with the disk
            // bound in mind.
            File.Delete(archivePath);

            // A flat-rooted archive (the T-125 shape) extracts as itself; an
            // archive that wrapped its contents in one folder normalizes by
            // lifting that folder's content one level — the staged build is
            // always the direct child set the swap expects.
            var entries = Directory.GetFileSystemEntries(versionDirectory);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
            {
                var wrapper = entries[0];
                foreach (var inner in Directory.GetFileSystemEntries(wrapper))
                {
                    File.Move(inner, Path.Combine(versionDirectory, Path.GetFileName(inner)));
                }

                Directory.Delete(wrapper);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateStagingException(UpdateStageFailure.ExtractFault, ex.Message);
        }
    }

    /// <summary>Best-effort removal of a refused attempt's partial artifacts.</summary>
    private void CleanupPartialStaging(string version)
    {
        try
        {
            var directory = _store.StagedDirectory(version);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The refused staging's partial artifacts for {Version} could not delete now", version);
        }
    }

    /// <summary>
    /// The staging tree holds ONE candidate: a successfully staged version
    /// sweeps the siblings (an earlier declined or superseded stage) —
    /// best-effort, the disk bound keeps the tree bounded.
    /// </summary>
    private void SweepSiblingStages(string version)
    {
        try
        {
            foreach (var sibling in Directory.GetDirectories(_store.StagedRootDirectory))
            {
                if (!string.Equals(Path.GetFileName(sibling), version, StringComparison.Ordinal))
                {
                    Directory.Delete(sibling, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "A superseded staged build could not delete now; the next sweep catches it");
        }
    }

    #endregion The staging

    #region The bounded download

    /// <summary>Fetch the archive into a file under the size cap and the total budget.</summary>
    private Task DownloadArchiveAsync(UpdateAsset asset, string targetPath, CancellationToken ct) =>
        DownloadAsync(asset, targetPath, null, MaxDownloadBytes, ct);

    /// <summary>Fetch the small sums file into text under its cap (the same bounded fetch, memory lane).</summary>
    private async Task<string> DownloadHashTextAsync(UpdateAsset asset, CancellationToken ct)
    {
        var sink = new MemoryStream();
        await DownloadAsync(asset, null, sink, MaxHashFileBytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(sink.ToArray());
    }

    /// <summary>
    /// The one bounded fetch every download rides: HTTPS pinned before and
    /// after (the landing URI included — a redirect that left HTTPS fails),
    /// the header-checked size cap, the streamed byte cap, and the total
    /// budget that cuts a stalled transfer.
    /// </summary>
    private async Task DownloadAsync(
        UpdateAsset asset,
        string? filePath,
        MemoryStream? memorySink,
        long cap,
        CancellationToken ct)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(DownloadBudget);
        var budget = budgetCts.Token;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(asset.DownloadUrl));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget)
            .ConfigureAwait(false);
        EnsureHttpsLanding(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateStagingException(
                UpdateStageFailure.DownloadFault,
                $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is { } length && length > cap)
        {
            throw new UpdateStagingException(
                UpdateStageFailure.TooLarge,
                length.ToString(CultureInfo.InvariantCulture));
        }

        await using var source = await response.Content.ReadAsStreamAsync(budget).ConfigureAwait(false);
        await using Stream target = filePath is not null
            ? new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None)
            : memorySink ?? throw new InvalidOperationException("A download carries a file target or a memory sink.");

        var buffer = new byte[81920];
        int read;
        long total = 0;
        while ((read = await source.ReadAsync(buffer, budget).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new UpdateStagingException(
                    UpdateStageFailure.TooLarge,
                    total.ToString(CultureInfo.InvariantCulture));
            }

            await target.WriteAsync(buffer.AsMemory(0, read), budget).ConfigureAwait(false);
        }
    }

    /// <summary>Whether one update URL is an absolute HTTPS URL (the only scheme the loop ever fetches).</summary>
    private static void EnsureHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateStagingException(UpdateStageFailure.NotHttps, url);
        }
    }

    /// <summary>
    /// Whether the request's landing URI is still HTTPS (the honest
    /// downgrade refusal). The redirect-following handler mutates the
    /// request's OWN uri to the landing address on every hop, and the
    /// .NET 10 send path no longer echoes it on the response — so the
    /// check reads the request itself, after the send returned.
    /// </summary>
    private static void EnsureHttpsLanding(HttpRequestMessage request)
    {
        var landing = request.RequestUri;
        if (landing is null || landing.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateStagingException(UpdateStageFailure.NotHttps, landing?.ToString() ?? "(no landing uri)");
        }
    }

    #endregion The bounded download

    #region The consent gate

    /// <summary>
    /// The ONE consent question per version: the disclosure composes from
    /// the venues' LIVE state — how many open positions and working orders
    /// the session holds, and which venues could not be read — so the user
    /// decides about a restart with the trading state in front of them. The
    /// ask marshals onto the UI thread (the dialog's home); a refusal, a
    /// timeout or a failed ask leaves the manifest <c>Staged</c> — nothing
    /// applies without the answer.
    /// </summary>
    private async Task AskConsentAsync(UpdatePendingManifest manifest, CancellationToken ct)
    {
        var disclosure = await ReadSessionTradingStateAsync(ct).ConfigureAwait(false);
        var request = ComposeConsent(manifest.Version, disclosure);

        var approved = await _uiMarshaller(
            () => _confirmations.RequestConfirmationAsync(request, CancellationToken.None)).ConfigureAwait(false);

        if (approved)
        {
            _store.Write(manifest with { State = UpdateSwapState.Approved });
            _logger.LogInformation(
                "The consent for {Version} landed: the update applies on the next start ({Positions} open, {Orders} working)",
                manifest.Version,
                disclosure.Positions,
                disclosure.WorkingOrders);
        }
        else
        {
            _logger.LogInformation(
                "The consent for {Version} was refused or timed out: the update stays staged, nothing applies",
                manifest.Version);
        }
    }

    /// <summary>Read the trading state the disclosure speaks: per-venue live counts, unreachable venues named.</summary>
    private async Task<SessionTradingDisclosure> ReadSessionTradingStateAsync(CancellationToken ct)
    {
        var positions = 0;
        var workingOrders = 0;
        var unreachable = new List<string>();
        foreach (var venue in _venues.Venues)
        {
            try
            {
                positions += (await venue.GetPositionsAsync(ct).ConfigureAwait(false))
                    .Count(position => position.Quantity.Value > decimal.Zero);
                workingOrders += (await venue.GetOpenOrdersAsync(ct).ConfigureAwait(false))
                    .Count(order => order.Status
                        is Core.Enums.OrderStatus.Pending
                        or Core.Enums.OrderStatus.Submitted
                        or Core.Enums.OrderStatus.PartiallyFilled);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "The consent disclosure could not read {VenueName}; the venue names itself unreachable",
                    venue.Name);
                unreachable.Add(venue.Name);
            }
        }

        return new SessionTradingDisclosure(positions, workingOrders, unreachable);
    }

    /// <summary>Compose the consent question: the version, the disclosure line, the unreachable note.</summary>
    private ConfirmationRequest ComposeConsent(string version, SessionTradingDisclosure disclosure)
    {
        var positionsText = TextOfCount(disclosure.Positions);
        var ordersText = TextOfCount(disclosure.WorkingOrders);
        var riskLine = _localization.GetString(ConsentRiskLineKey, positionsText, ordersText);
        var unreachableNote = disclosure.Unreachable.Count == 0
            ? string.Empty
            : _localization.GetString(ConsentUnreachableKey, string.Join(", ", disclosure.Unreachable));
        return new ConfirmationRequest
        {
            MessageKey = ConsentFormatKey,
            Arguments = [version, riskLine, unreachableNote],
        };
    }

    /// <summary>A disclosure slot's text: the invariant count, or the localized none.</summary>
    private string TextOfCount(int count) =>
        count == 0
            ? _localization.GetString(ConsentNoneKey)
            : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Run the ask on the UI thread: an already-UI caller runs inline, a background caller hops and awaits (the test seam pins its own hop).</summary>
    private static Task<bool> AskOnUi(Func<Task<bool>> ask) =>
        Dispatcher.UIThread.CheckAccess()
            ? ask()
            : Dispatcher.UIThread.InvokeAsync(ask);

    #endregion The consent gate

    #region Records

    /// <summary>The trading state one consent disclosure speaks.</summary>
    private sealed record SessionTradingDisclosure(int Positions, int WorkingOrders, IReadOnlyList<string> Unreachable);

    #endregion Records

    #region Resx Keys

    private const string ConsentFormatKey = "Update.Consent.Format";
    private const string ConsentRiskLineKey = "Update.Consent.RiskLine";
    private const string ConsentUnreachableKey = "Update.Consent.Unreachable";
    private const string ConsentNoneKey = "Update.Consent.None";

    #endregion Resx Keys

    #region Helpers

    /// <summary>
    /// The platform's RID spellings the T-125 delivery pipeline publishes
    /// one artifact for (one OS + architecture leg each): the process
    /// architecture names the exact candidate.
    /// </summary>
    private static IReadOnlyList<string> DefaultRidCandidates()
    {
        var architecture = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 ? "arm64" : "x64";
        return OperatingSystem.IsWindows()
            ? [$"win-{architecture}"]
            : OperatingSystem.IsMacOS()
                ? [$"osx-{architecture}"]
                : [$"linux-{architecture}"];
    }

    #endregion Helpers
}
