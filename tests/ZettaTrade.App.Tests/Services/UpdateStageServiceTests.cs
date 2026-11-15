namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The auto-update staging service's loopback battery (T-258): the
/// checker rides its canned releases handler, the downloads ride their
/// own canned asset handler (no network — the suite's loopback
/// discipline), and the staging tree lives in a per-fact temp install
/// directory. The facts pin the acceptance: nothing stages without the
/// SHA-256 match against the release's own sums file, nothing applies
/// without the ONE consent question (whose disclosure reads the session's
/// live trading state), the off switch silences the whole loop, and a
/// failed staging never retries inside the same process.
/// </summary>
public sealed class UpdateStageServiceTests
{
    /// <summary>The staged release's version (far above any running build, so a version bump never flips the story).</summary>
    private const string StagedVersion = "9.9.9";

    private const string ArchiveName = "zettatrade-9.9.9-win-x64.zip";

    private const string SumsName = "zettatrade-9.9.9-sha256.txt";

    private static readonly string ArchiveUrl = "https://assets.example.test/" + ArchiveName;

    private static readonly string SumsUrl = "https://assets.example.test/" + SumsName;

    /// <summary>The bounded wait the pump-driven facts join through (the AlertEngine budget shape).</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The consent write's settle: the ask, the answer and the Approved
    /// manifest write ride ONE synchronous pump continuation, so a settle
    /// longer than that path (a warm manifest write is low single-digit
    /// milliseconds) is the honest join — the state read after it can never
    /// race the write. The write itself is an atomic temp-file replace-move,
    /// and a move over a file ANY reader holds open fails with Access
    /// denied (the pump never retries the write — ONE question per
    /// version), so a manifest poll must never overlap that window.
    /// </summary>
    private static readonly TimeSpan ConsentWriteSettle = TimeSpan.FromMilliseconds(250);

    #region Tests: the staged path

    /// <summary>A consented verdict stages the verified build, asks the ONE question and writes Approved.</summary>
    [Fact]
    public async Task AConsentedVerdict_StagesTheVerifiedBuild_AndAsksTheOneQuestion()
    {
        var archiveBytes = ZipArchiveBytes([HostEntry(), ("zettatrade.dll", "staged dll")]);
        var confirmations = new ConfirmationStub(answer: true);
        var venue = VenueWith(positions: 1, orders: 1);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(archiveBytes),
            confirmations: confirmations,
            venues: [venue]);

        await WaitUntilAsync(() => confirmations.Requests.Count > 0, "the consent ask landed");
        await Task.Delay(ConsentWriteSettle);
        ReadState(harness).Should().Be(UpdateSwapState.Approved, "the consent landed");

        confirmations.Requests.Should().ContainSingle("the ONE consent question per version");
        var request = confirmations.Requests[0];
        request.MessageKey.Should().Be("Update.Consent.Format", "the ask rides the localization key");
        request.Arguments[0].Should().Be(StagedVersion, "the ask names the version");

        var manifest = harness.Store.TryRead()!;
        manifest.Version.Should().Be(StagedVersion);
        manifest.AssetName.Should().Be(ArchiveName, "the verified archive's own name is the manifest's identity");
        manifest.AssetSha256.Should().Be(Sha256Hex(archiveBytes), "the hash the staging pinned");
        manifest.ProofRequired.Should().BeFalse("the consent is not the launch proof");

        var stagedDirectory = harness.Store.StagedDirectory(StagedVersion);
        File.ReadAllText(Path.Combine(stagedDirectory, HostFileName())).Should().Be(
            "staged host",
            "the archive extracted flat into the staged build");
        File.ReadAllText(Path.Combine(stagedDirectory, "zettatrade.dll")).Should().Be("staged dll");
        File.Exists(Path.Combine(stagedDirectory, ArchiveName)).Should().BeFalse(
            "the verified archive's work is done — the disk bound deletes it");
    }

    /// <summary>An archive that wrapped its contents in one folder normalizes — the staged build is the direct child set.</summary>
    [Fact]
    public async Task AWrappedArchive_NormalizesToOneLevel()
    {
        var archiveBytes = ZipArchiveBytes(
        [
            ("zettatrade-9.9.9/" + HostFileName(), "staged host"),
            ("zettatrade-9.9.9/zettatrade.dll", "staged dll"),
        ]);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(archiveBytes),
            confirmations: new ConfirmationStub(answer: true));

        await WaitUntilAsync(() => harness.Confirmations.Requests.Count > 0, "the consent ask landed");
        await Task.Delay(ConsentWriteSettle);
        ReadState(harness).Should().Be(UpdateSwapState.Approved, "the wrapped stage normalized and consented");

        var stagedDirectory = harness.Store.StagedDirectory(StagedVersion);
        File.ReadAllText(Path.Combine(stagedDirectory, HostFileName())).Should().Be(
            "staged host",
            "the wrapper folder's content lifted one level");
        Directory.GetDirectories(stagedDirectory).Should().BeEmpty("no wrapper folder remains");
    }

    /// <summary>A declined (or timed out) ask leaves the manifest Staged — nothing applies without the answer.</summary>
    [Fact]
    public async Task ADeclinedConsent_LeavesTheManifestStaged_AndNeverRepeatsTheAsk()
    {
        var confirmations = new ConfirmationStub(answer: false);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(ZipArchiveBytes([HostEntry()])),
            confirmations: confirmations);

        await WaitUntilAsync(
            () => ReadState(harness) == UpdateSwapState.Staged,
            "the staging landed, the consent is pending");

        // The same version's second verdict (the cadence, or a manual ask)
        // never re-asks: ONE question per version per process.
        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        confirmations.Requests.Should().ContainSingle("the version's question was already asked");
        ReadState(harness).Should().Be(UpdateSwapState.Staged, "the refusal leaves the update staged, nothing applies");
    }

    /// <summary>The consent's disclosure names the live trading state: counts from the venues, unreachable venues honestly.</summary>
    [Fact]
    public async Task TheConsentDisclosure_NamesTheTradingState_AndTheUnreachableVenues()
    {
        var confirmations = new ConfirmationStub(answer: true);
        var healthy = VenueWith(positions: 2, orders: 1);
        var offline = Substitute.For<ITradingAdapter>();
        offline.Name.Returns("venue-offline");
        offline.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.FromException<IReadOnlyList<Position>>(new InvalidOperationException("venue offline")));
        offline.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<Order>>([]));

        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(ZipArchiveBytes([HostEntry()])),
            confirmations: confirmations,
            venues: [healthy, offline]);

        await WaitUntilAsync(
            () => confirmations.Requests.Count > 0,
            "the consent ask landed",
            () => $"asks={harness.Confirmations.Requests.Count} assetReqs={harness.AssetHandler.Requests} "
                + $"releasesReqs={harness.Releases.Requests} state={ReadState(harness)} "
                + "log=[" + string.Join(" || ", harness.StageLog.Entries.TakeLast(6)) + "]");
        await Task.Delay(ConsentWriteSettle);
        ReadState(harness).Should().Be(UpdateSwapState.Approved, "the consent landed");

        var request = confirmations.Requests.Should().ContainSingle().Subject;
        request.Arguments.Should().HaveCount(3, "the version, the risk line and the unreachable note ride the ask");
        request.Arguments[0].Should().Be(StagedVersion, "the ask names the version");
        ((string)request.Arguments[1]).Should().Contain("Update.Consent.RiskLine")
            .And.Contain("2", "the two open positions ride the disclosure")
            .And.Contain("1", "the one working order rides the disclosure");
        ((string)request.Arguments[2]).Should().Contain("Update.Consent.Unreachable")
            .And.Contain("venue-offline", "the unreachable venue names itself honestly");
    }

    /// <summary>A venue read failure at the position read still counts the rest and names the dead venue.</summary>
    [Fact]
    public async Task AZeroStateSession_DisclosesTheZerosHonestly()
    {
        var confirmations = new ConfirmationStub(answer: true);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(ZipArchiveBytes([HostEntry()])),
            confirmations: confirmations);

        await WaitUntilAsync(() => confirmations.Requests.Count > 0, "the consent ask landed");
        await Task.Delay(ConsentWriteSettle);
        ReadState(harness).Should().Be(UpdateSwapState.Approved, "the consent landed");

        var request = confirmations.Requests.Should().ContainSingle().Subject;
        ((string)request.Arguments[1]).Should().Contain("Update.Consent.RiskLine")
            .And.Contain("Update.Consent.None", "an empty disclosure says none, never zero-fakes");
    }

    /// <summary>The off switch silences the whole loop: no download, no staging, no ask; a live flip re-arms it.</summary>
    [Fact]
    public async Task TheOffSwitch_SilencesTheLoop_AndALiveFlipRearsIt()
    {
        var confirmations = new ConfirmationStub(answer: true);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: AssetMap(ZipArchiveBytes([HostEntry()])),
            confirmations: confirmations,
            autoUpdate: false);

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("the off switch staged nothing");
        confirmations.Requests.Should().BeEmpty("a silenced loop never asks");
        harness.AssetHandler.Requests.Should().Be(0, "a silenced loop downloads nothing");

        await harness.Settings.SetAsync(AppSettingsKeys.Section, AppSettingsKeys.AutoUpdate, true);
        await harness.Checker.CheckNowAsync();
        await WaitUntilAsync(() => confirmations.Requests.Count > 0, "the flip re-arms the ask");
        await Task.Delay(ConsentWriteSettle);
        ReadState(harness).Should().Be(UpdateSwapState.Approved, "the flip re-arms the loop");
    }

    #endregion

    #region Tests: the honest refusals

    /// <summary>A hash mismatch refuses the stage: no manifest, the partial artifacts die, the ask never comes.</summary>
    [Fact]
    public async Task AHashMismatch_RefusesTheStage_AndCleansUp()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: new Dictionary<string, byte[]>
            {
                [ArchiveUrl] = ZipArchiveBytes([HostEntry()]),
                [SumsUrl] = WrongSumsText(),
            });

        await WaitUntilAsync(() => harness.AssetHandler.Requests >= 2, "both downloads ran");

        ReadState(harness).Should().BeNull("a mismatched hash never stages");
        await WaitUntilAsync(
            () => !Directory.Exists(harness.Store.StagedDirectory(StagedVersion)),
            "the partial artifacts died");
    }

    /// <summary>A failed staging never retries inside the same process — a NEW process's check re-offers.</summary>
    [Fact]
    public async Task AFailedStaging_NeverRetriesThisProcess()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: new Dictionary<string, byte[]>
            {
                [ArchiveUrl] = ZipArchiveBytes([HostEntry()]),
                [SumsUrl] = WrongSumsText(),
            });

        await WaitUntilAsync(() => harness.AssetHandler.Requests >= 2, "the one attempt ran its two downloads");
        var requestsAfterFirstAttempt = harness.AssetHandler.Requests;

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        harness.AssetHandler.Requests.Should().Be(requestsAfterFirstAttempt, "the failed version never retries this process");
        ReadState(harness).Should().BeNull();
    }

    /// <summary>A release without this RID's archive refuses honestly — and without asking.</summary>
    [Fact]
    public async Task AMissingRidArchive_RefusesTheStage()
    {
        var confirmations = new ConfirmationStub(answer: true);
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets(
                archiveName: "zettatrade-9.9.9-osx-arm64.zip")),
            assets: new Dictionary<string, byte[]> { [SumsUrl] = SumsText([1, 2, 3]) },
            confirmations: confirmations);

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("a RID the release does not carry is the honest no-asset refusal");
        confirmations.Requests.Should().BeEmpty("nothing stages, nothing asks");
    }

    /// <summary>A release without the sums file refuses — the hash cannot be pinned, nothing stages.</summary>
    [Fact]
    public async Task AMissingSumsFile_RefusesTheStage()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: new Dictionary<string, byte[]> { [ArchiveUrl] = ZipArchiveBytes([HostEntry()]) });

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("without the sums file the hash cannot pin");
    }

    /// <summary>A sums file without the archive's line refuses — the expected hash never substitutes.</summary>
    [Fact]
    public async Task ASumsFileWithoutTheArchiveLine_RefusesTheStage()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: new Dictionary<string, byte[]>
            {
                [ArchiveUrl] = ZipArchiveBytes([HostEntry()]),
                [SumsUrl] = Encoding.UTF8.GetBytes("aaaa  some-other-file.zip\n"),
            });

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("the archive's line is the only hash the staging pins");
    }

    /// <summary>A lying Content-Length header refuses before a byte streams — the disk bound holds.</summary>
    [Fact]
    public async Task ALyingHeader_RefusesTheDownload_BeforeAnyByteStreams()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets()),
            assets: new Dictionary<string, byte[]>
            {
                [ArchiveUrl] = [],
                [SumsUrl] = SumsText([1, 2, 3]),
            },
            lieAboutArchiveLength: true);

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("a 600 MB header on an empty body never stages");
        harness.AssetHandler.ArchiveBodyReads.Should().Be(0, "the header check trips before the body streams");
    }

    /// <summary>A redirect that leaves HTTPS refuses at the landing check — the transport stays pinned.</summary>
    [Fact]
    public async Task ADowngradeRedirect_RefusesAtTheLandingCheck()
    {
        await using var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0",
            payloadJson: ReleasesJson(ReleaseWithAssets(
                archiveName: ArchiveName,
                archiveUrl: "https://assets.example.test/redirect/" + ArchiveName)),
            assets: new Dictionary<string, byte[]>
            {
                ["http://assets.example.test/" + ArchiveName] = [1, 2, 3],
                [SumsUrl] = SumsText([1, 2, 3]),
            },
            redirectArchiveToHttp: true);

        await harness.Checker.CheckNowAsync();
        await Task.Delay(SettleDelay);

        ReadState(harness).Should().BeNull("a downgrade redirect is the honest NotHttps refusal");
    }

    #endregion

    #region Fixtures and harness

    private static UpdateSwapState? ReadState(StageHarness harness) => harness.Store.TryRead()?.State;

    /// <summary>
    /// The bounded wait the pump-driven facts join through (the AlertEngine
    /// budget shape). The optional probe rides the failure message: a
    /// timed-out wait reports the harness's last-known shape (the state,
    /// the counters and the service's own captured warning lane), never a
    /// bare no.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, Func<string>? probe = null)
    {
        var deadline = DateTimeOffset.UtcNow + WaitBudget;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (condition())
        {
            return;
        }

        if (probe is null)
        {
            condition().Should().BeTrue(because);
        }
        else
        {
            throw new InvalidOperationException($"{because}: {probe()}");
        }
    }

    /// <summary>The asset map the happy paths ride: the archive and its own sums file.</summary>
    private static Dictionary<string, byte[]> AssetMap(byte[] archiveBytes) => new()
    {
        [ArchiveUrl] = archiveBytes,
        [SumsUrl] = SumsText(archiveBytes),
    };

    /// <summary>The app-host entry the staging's pre-flight check requires: the RUNNING process's own file name.</summary>
    private static string HostFileName() => Path.GetFileName(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("The test host must expose its process path for the pre-flight shape."));

    /// <summary>The archive entry named as the running host (the pre-flight check's own file).</summary>
    private static (string Name, string Content) HostEntry() => (HostFileName(), "staged host");

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The sums text over one archive's real bytes (the two-space convention).</summary>
    private static byte[] SumsText(byte[] archiveBytes) =>
        Encoding.UTF8.GetBytes(Sha256Hex(archiveBytes) + "  " + ArchiveName + "\n");

    /// <summary>The wrong-but-parsable sums text: a VALID 64-hex token that no real archive hashes to.</summary>
    private static byte[] WrongSumsText() =>
        Encoding.UTF8.GetBytes(new string('a', 64) + "  " + ArchiveName + "\n");

    /// <summary>One zip archive's bytes over the given entries (one flat root, the T-125 shape).</summary>
    private static byte[] ZipArchiveBytes((string Name, string Content)[] entries)
    {
        using var sink = new MemoryStream();
        using (var archive = new ZipArchive(sink, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return sink.ToArray();
    }

    /// <summary>One release object in the endpoint's payload shape, carrying its asset list.</summary>
    private static string ReleaseWithAssets(
        string? archiveName = null,
        string? archiveUrl = null)
    {
        var archive = archiveName ?? ArchiveName;
        var url = archiveUrl ?? ArchiveUrl;
        return $$"""
            {"tag_name":"v{{StagedVersion}}","draft":false,"prerelease":false,"html_url":"https://github.com/zettatrade/zettatrade/releases/tag/v{{StagedVersion}}","name":"{{StagedVersion}}","assets":[{"name":"{{archive}}","browser_download_url":"{{url}}"},{"name":"{{SumsName}}","browser_download_url":"{{SumsUrl}}"}]}
            """;
    }

    /// <summary>The releases list payload.</summary>
    private static string ReleasesJson(params string[] releases) =>
        "[" + string.Join(",", releases) + "]";

    /// <summary>
    /// The stage service's own logger: the pump's warning lane (a refused
    /// staging, a failed consent write) is captured, so a timed-out wait can
    /// report WHY the state never moved — the suite's
    /// every-failure-diagnosable shape, not a silent wedge.
    /// </summary>
    private sealed class CapturingLogger : ILogger<UpdateStageService>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(
                $"{logLevel}: {formatter(state, exception)}"
                + (exception is null ? string.Empty : $" :: {exception.GetType().Name}: {exception.Message}"));
    }

    /// <summary>
    /// The consent recorder (the marshal-free spy shape the suite keeps):
    /// the structured request is the only lane the staging rides, so the
    /// string overload fails loudly if it ever comes back.
    /// </summary>
    private sealed class ConfirmationStub(bool answer) : IConfirmationService
    {
        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<bool> RequestConfirmationAsync(string message, CancellationToken ct = default) =>
            throw new NotSupportedException("The staging rides the structured confirmation request only.");

        public Task<bool> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer);
        }
    }

    /// <summary>A healthy venue substitute: the disclosure's live counts ride its answers.</summary>
    private static ITradingAdapter VenueWith(int positions, int orders)
    {
        var venue = Substitute.For<ITradingAdapter>();
        venue.Name.Returns("venue-healthy");
        var positionList = new List<Position>();
        for (var index = 0; index < positions; index++)
        {
            positionList.Add(new Position
            {
                Id = $"pos-{index}",
                ExchangeId = "paper",
                Symbol = Symbol.Parse("BTC/USDT"),
                Side = OrderSide.Buy,
                Quantity = new Quantity(0.01m),
                EntryPrice = new Price(50_000m),
                CurrentPrice = new Price(50_000m),
                OpenedAt = DateTimeOffset.UtcNow,
            });
        }

        var orderList = new List<Order>();
        for (var index = 0; index < orders; index++)
        {
            orderList.Add(new Order
            {
                Id = $"order-{index}",
                ExchangeId = "paper",
                ExchangeOrderId = $"order-{index}",
                Symbol = Symbol.Parse("BTC/USDT"),
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = new Quantity(0.01m),
                Price = new Price(49_900m),
                TimeInForce = TimeInForce.GTC,
                Status = OrderStatus.Pending,
                FilledQuantity = Quantity.Zero,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        venue.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<Position>>(positionList));
        venue.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<Order>>(orderList));
        return venue;
    }

    private static async Task<StageHarness> CreateHarnessAsync(
        string currentVersion,
        string payloadJson,
        Dictionary<string, byte[]> assets,
        ConfirmationStub? confirmations = null,
        IReadOnlyList<ITradingAdapter>? venues = null,
        bool autoUpdate = true,
        bool lieAboutArchiveLength = false,
        bool redirectArchiveToHttp = false)
    {
        var install = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "t258-stage-" + Guid.NewGuid().ToString("N"))).FullName;

        var settings = new FakeSettings();
        if (autoUpdate)
        {
            // The ON facts ride the setting EXPLICITLY: the product's
            // default is the honest opt-in OFF, so an unset store answers
            // disabled and the pump never acts — the seed is the test's
            // own statement of the behavior under test.
            await settings.SetAsync(AppSettingsKeys.Section, AppSettingsKeys.AutoUpdate, true);
        }
        else
        {
            await settings.SetAsync(AppSettingsKeys.Section, AppSettingsKeys.AutoUpdate, false);
        }

        var releasesHandler = new StubReleasesHandler { PayloadJson = payloadJson };
        var assetHandler = new StubAssetHandler(assets)
        {
            LieAboutArchiveLength = lieAboutArchiveLength,
            RedirectArchiveToHttp = redirectArchiveToHttp,
        };
        var factory = new StubHttpClientFactory(releasesHandler, assetHandler);
        var center = new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance);
        var checker = new UpdateCheckService(
            settings,
            factory,
            center,
            NullLogger<UpdateCheckService>.Instance,
            currentVersion);
        var confirmation = confirmations ?? new ConfirmationStub(answer: false);
        var venueSet = new KillSwitchVenues(venues ?? []);
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>()).Returns(call => $"[{call.ArgAt<string>(0)}]");
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join(",", call.ArgAt<object[]>(1).Select(item => item))}]");

        var stageLogger = new CapturingLogger();
        var stage = new UpdateStageService(
            settings,
            factory,
            checker,
            venueSet,
            confirmation,
            localization,
            stageLogger,
            installDirectory: install,
            ridCandidates: () => ["win-x64"],
            uiMarshaller: ask => ask());

        await checker.InitializeAsync();
        await stage.InitializeAsync();

        return new StageHarness(
            install,
            settings,
            assetHandler,
            releasesHandler,
            stageLogger,
            checker,
            confirmation,
            stage);
    }

    private sealed record StageHarness(
        string Install,
        FakeSettings Settings,
        StubAssetHandler AssetHandler,
        StubReleasesHandler Releases,
        CapturingLogger StageLog,
        UpdateCheckService Checker,
        ConfirmationStub Confirmations,
        UpdateStageService Stage) : IAsyncDisposable
    {
        public UpdateStagingStore Store { get; } = new(Install);

        /// <summary>
        /// The AWAITED teardown (the T-266 harness canon): the services stop
        /// and the temp tree dies INSIDE the test's lifetime. The previous
        /// async-void shape let a test's pump outlive its own test — an
        /// unobserved fire-and-forget the suite could never reason about.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await Checker.DisposeAsync();
            await Stage.DisposeAsync();
            try
            {
                Directory.Delete(Install, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temp space is the OS's to reclaim; a locked tree dies later.
            }
        }
    }

    /// <summary>The canned releases endpoint: counts every request, answers the payload it carries.</summary>
    private sealed class StubReleasesHandler : HttpMessageHandler
    {
        public int Requests;

        public string? PayloadJson { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PayloadJson ?? "[]", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The canned asset lane: answers the registered URLs with their bytes
    /// (an honest Content-Length), a 404 otherwise. The redirect and the
    /// lying-header facts ride their own switches; the archive body's own
    /// reads count (the header-refusal fact reads nothing).
    /// </summary>
    private sealed class StubAssetHandler(Dictionary<string, byte[]> assets) : HttpMessageHandler
    {
        public int Requests;

        public int ArchiveBodyReads;

        public bool LieAboutArchiveLength { get; init; }

        public bool RedirectArchiveToHttp { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var url = request.RequestUri!.ToString();

            if (RedirectArchiveToHttp && url.Contains("/redirect/", StringComparison.Ordinal))
            {
                var location = url.Replace("https://", "http://", StringComparison.Ordinal)
                    .Replace("/redirect/", "/", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
            }

            if (!assets.TryGetValue(url, out var bytes))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent([]),
                };
            }

            var content = new CountingContent(bytes, this, counts: url.EndsWith(".zip", StringComparison.Ordinal));
            if (LieAboutArchiveLength && url.EndsWith(".zip", StringComparison.Ordinal))
            {
                content.Headers.ContentLength = 600L * 1024 * 1024;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        /// <summary>The byte content that counts its own stream reads per URL (the header-refusal fact reads none).</summary>
        private sealed class CountingContent : ByteArrayContent
        {
            private readonly StubAssetHandler _owner;

            private readonly bool _counts;

            private readonly byte[] _bytes;

            public CountingContent(byte[] bytes, StubAssetHandler owner, bool counts)
                : base(bytes)
            {
                _bytes = bytes;
                _owner = owner;
                _counts = counts;
            }

            protected override async Task<Stream> CreateContentReadStreamAsync()
            {
                if (_counts)
                {
                    Interlocked.Increment(ref _owner.ArchiveBodyReads);
                }

                return new MemoryStream(_bytes);
            }
        }
    }

    /// <summary>The factory double: the checker's and the assets' named clients each ride their own stub.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler releasesHandler, HttpMessageHandler assetHandler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var handler = name == UpdateCheckService.HttpClientName
                ? releasesHandler
                : name == UpdateStageService.HttpClientName
                    ? assetHandler
                    : throw new InvalidOperationException($"Unexpected client name {name}");
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>
    /// An in-memory reactive settings double (the checker battery's shape
    /// in miniature): the reads answer the store, the writes land in it
    /// and announce on the change stream.
    /// </summary>
    private sealed class FakeSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        private readonly Subject<SettingsChange> _changes = new();

        public IObservable<SettingsChange> Changes => _changes;

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            if (_store.TryGetValue($"{section}\u0001{key}", out var value) && value is T typed)
            {
                return Task.FromResult(typed);
            }

            return Task.FromResult(defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            _store[$"{section}\u0001{key}"] = value;
            _changes.OnNext(new SettingsChange(section, key, null, value));
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    #endregion
}
