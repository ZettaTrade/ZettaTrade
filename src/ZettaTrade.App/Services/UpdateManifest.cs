namespace ZettaTrade.App.Services;

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
/// <summary>The pending update's lifecycle state (the T-258 swap state machine).</summary>
public enum UpdateSwapState
{
    /// <summary>The build is staged and verified; the consent question is pending.</summary>
    Staged = 0,

    /// <summary>The user consented: the swap may run at the next start.</summary>
    Approved = 1,

    /// <summary>The swap ran; the new build is on disk and has not yet proven healthy.</summary>
    Applied = 2,

    /// <summary>The new build launched and reached the healthy point; the update is done.</summary>
    Settled = 3,
}

/// <summary>
/// The pending update's manifest of record: the staged version, the state
/// the swap machine stands at, the verified asset's identity and the moment
/// the staging landed. The file is the ONLY authority the swap runner acts
/// on — a missing or unparsable manifest is the honest no-pending-update
/// state, never a guessed swap.
/// </summary>
/// <param name="Version">The staged build's display version.</param>
/// <param name="State">The swap machine's state.</param>
/// <param name="ProofRequired">Whether the applied build still owes the healthy-launch proof.</param>
/// <param name="AssetName">The verified archive asset's own name.</param>
/// <param name="AssetSha256">The verified archive's SHA-256 (the hash the staging pinned).</param>
/// <param name="StagedUtc">The moment the staging completed (UTC).</param>
public sealed record UpdatePendingManifest(
    string Version,
    UpdateSwapState State,
    bool ProofRequired,
    string AssetName,
    string AssetSha256,
    DateTimeOffset StagedUtc);

/// <summary>
/// The staging layout's store: where the pending update's files live inside
/// the install directory, and the manifest's atomic reads and writes. The
/// layout keeps EVERYTHING the swap touches inside one <c>.update</c>
/// subtree of the install directory — the user's data lives in the profile
/// directories (config, data, state), so a swap can never touch it.
/// <para>
/// Every manifest write is atomic (a temp file + a same-directory move), so
/// a crash mid-write can never leave a half manifest driving a swap: a
/// reader either sees the previous complete file or the new one. An
/// unparsable file is the honest none state for decisions — only the startup
/// runner deletes one.
/// </para>
/// </summary>
public sealed class UpdateStagingStore
{
    #region Constants

    /// <summary>The staging subtree's name inside the install directory.</summary>
    public const string StagingDirectoryName = ".update";

    /// <summary>The pending manifest's file name inside the staging subtree.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The previous build's parking spot during an applied window.</summary>
    public const string RollbackDirectoryName = "rollback";

    /// <summary>The staged builds' root (one version per child directory).</summary>
    public const string StagedDirectoryName = "staged";

    /// <summary>The failed new build's discard root (the rollback's moving-aside spot).</summary>
    public const string DiscardDirectoryName = "discard";

    #endregion Constants

    #region Private Fields

    /// <summary>The install directory the store serves (absolute, per instance).</summary>
    private readonly string _installDirectory;

    /// <summary>The atomic manifest writes' serializer options.</summary>
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The tolerant reads' options (case-insensitive names, unknown fields ignored).</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    #endregion Private Fields

    #region Construction

    /// <summary>Create the store over one install directory.</summary>
    /// <param name="installDirectory">The app's install directory (the exe's directory).</param>
    public UpdateStagingStore(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("The install directory must be a real path.", nameof(installDirectory));
        }

        _installDirectory = Path.GetFullPath(installDirectory);
    }

    #endregion Construction

    #region Paths

    /// <summary>The install directory the store serves.</summary>
    public string InstallDirectory => _installDirectory;

    /// <summary>The staging subtree's root: the install directory's <c>.update</c>.</summary>
    public string StagingDirectory => Path.Combine(_installDirectory, StagingDirectoryName);

    /// <summary>The pending manifest's file path.</summary>
    public string ManifestPath => Path.Combine(StagingDirectory, ManifestFileName);

    /// <summary>The previous build's parking spot.</summary>
    public string RollbackDirectory => Path.Combine(StagingDirectory, RollbackDirectoryName);

    /// <summary>The staged builds' root.</summary>
    public string StagedRootDirectory => Path.Combine(StagingDirectory, StagedDirectoryName);

    /// <summary>One version's staged build directory.</summary>
    public string StagedDirectory(string version) => Path.Combine(StagedRootDirectory, version);

    /// <summary>The failed new builds' discard root.</summary>
    public string DiscardRootDirectory => Path.Combine(StagingDirectory, DiscardDirectoryName);

    /// <summary>
    /// One timestamped discard directory (the rollback's moving-aside spot).
    /// The name carries a short unique tail, so two attempts inside one
    /// millisecond never share a spot.
    /// </summary>
    public string NewDiscardDirectory() =>
        Path.Combine(
            DiscardRootDirectory,
            $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}");

    #endregion Paths

    #region The manifest

    /// <summary>
    /// Read the pending manifest; null when the file is absent or unparsable
    /// (the honest no-pending-update state for decisions). A manifest whose
    /// version or state does not parse is treated the same way — the file
    /// stays on disk for the startup runner to judge and delete.
    /// </summary>
    public UpdatePendingManifest? TryRead()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return null;
            }

            using var stream = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = JsonSerializer.Deserialize<UpdatePendingManifest>(stream, ReadOptions);
            return manifest is null
                || string.IsNullOrWhiteSpace(manifest.Version)
                || manifest.State is not (UpdateSwapState.Staged or UpdateSwapState.Approved or UpdateSwapState.Applied or UpdateSwapState.Settled)
                ? null
                : manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write the manifest atomically: the temp file lands in the staging
    /// directory first (the move is same-volume and either completes or
    /// leaves the previous manifest standing).
    /// </summary>
    public void Write(UpdatePendingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(StagingDirectory);

        var tempPath = ManifestPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, ManifestOptions);
            stream.Flush();
        }

        File.Move(tempPath, ManifestPath, overwrite: true);
    }

    #endregion The manifest

    #region Hygiene

    /// <summary>
    /// Sweep the staging subtree's orphans: staged directories no manifest
    /// references (a crashed staging's remains) and any temp manifest. The
    /// sweep runs only when NO manifest exists — a referenced staged
    /// directory is never touched.
    /// </summary>
    public void SweepOrphans()
    {
        if (TryRead() is not null)
        {
            return;
        }

        TryDeleteFile(ManifestPath + ".tmp");
        TryDeleteDirectory(StagedRootDirectory);
        TryDeleteDirectory(DiscardRootDirectory);
    }

    /// <summary>
    /// The settled update's cleanup: every pending-state spot inside the
    /// staging subtree (the manifest, the rollback parking spot, the staged
    /// build, any discard remains) is done — the whole <c>.update</c> tree
    /// goes, the install directory stands as the healthy authority.
    /// </summary>
    public void CleanupSettled() => TryDeleteDirectory(StagingDirectory);

    /// <summary>Delete one file if it exists (a locked file keeps the sweep honest — the next pass retries).</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweep is best-effort: a locked file dies on a later pass.
        }
    }

    /// <summary>Delete one directory tree if it exists (the same best-effort discipline).</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweep is best-effort: a locked tree dies on a later pass.
        }
    }

    #endregion Hygiene
}
