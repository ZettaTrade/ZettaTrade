namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The update checker's loopback battery (T-213): the releases stub is a
/// canned HttpMessageHandler (no network — the "loopback" discipline the
/// HTTP surfaces of the suite keep), the verdicts drive the stream and
/// the announcement, and the degraded states (a failing endpoint, a
/// malformed payload, the off switch) stay exactly as honest as the
/// acceptance names: nothing emits, nothing requests, the standing
/// verdict never flaps. The feed's no-release 404 is the opposite
/// shape: the honest no-update verdict, a completed check (T-230).
/// </summary>
/// <remarks>
/// Every fact drives the checker deterministically: both lanes subscribe
/// BEFORE InitializeAsync (the verdict stream's initial replay is its
/// known null baseline), the boot check is joined through the test seam
/// (<see cref="UpdateCheckService.FirstCheckForTests"/>), the cadence
/// (12 h) never ticks inside a test, and the manual
/// <see cref="UpdateCheckService.CheckNowAsync"/> is the one driver.
/// </remarks>
public sealed class UpdateCheckServiceTests
{
    #region Tests: the announcement path

    /// <summary>A newer release emits its verdict and announces exactly once — a repeated check stays silent on the toast.</summary>
    [Fact]
    public async Task ANewerRelease_EmitsAndAnnouncesOnce_AndTheRepeatStaysSilent()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159")));

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);
        var events = new List<Notification>();
        using var eventsSubscription = harness.Center.Events.Subscribe(events.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;
        await harness.Service.CheckNowAsync();
        await harness.Service.CheckNowAsync();

        // verdicts = the null replay + one verdict per COMPLETED check
        // (the boot check + the two manual asks); the announcement rides
        // the raw lane exactly once.
        verdicts.Should().HaveCount(4, "the replay + three completed checks each emit");
        verdicts[0].Should().BeNull("the stream replays its standing null to the early subscriber");
        verdicts.Skip(1).Select(verdict => verdict!.Version).Should().OnlyContain(
            version => version == "0.1.0-alpha.159",
            "the parsed tag and the release page are the verdict's own facts");
        verdicts[1].Should().BeEquivalentTo(
            new UpdateRelease("0.1.0-alpha.159", "https://github.com/zettatrade/zettatrade/releases/tag/v0.1.0-alpha.159"));
        harness.Service.Latest.Should().BeEquivalentTo(verdicts[3], "the standing verdict rides the same fact");

        events.Should().ContainSingle("the announcement dedupes per discovered version — the repeat checks never re-toast");
        events[0].Kind.Should().Be(NotificationKind.UpdateAvailable);
        events[0].Route.Should().Be(NotificationRoute.ReleasePage);
        events[0].VersionLabel.Should().Be("0.1.0-alpha.159");
        events[0].ReleaseUrl.Should().Be("https://github.com/zettatrade/zettatrade/releases/tag/v0.1.0-alpha.159");
        events[0].Count.Should().Be(1);
    }

    /// <summary>The endpoint offering nothing newer emits the honest empty verdict and announces nothing.</summary>
    [Fact]
    public async Task AnEqualOrOlderEndpoint_EmitsTheEmptyVerdict_AndAnnouncesNothing()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.159",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159"), Release("0.1.0-alpha.158")));

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);
        var events = new List<Notification>();
        using var eventsSubscription = harness.Center.Events.Subscribe(events.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;

        verdicts.Should().HaveCount(2, "the replay + the completed boot check");
        verdicts[1].Should().BeNull("the completed check's honest empty verdict clears the standing chip");
        harness.Service.Latest.Should().BeNull();
        events.Should().BeEmpty("nothing newer announces nothing");
    }

    /// <summary>The newest RELEASE ranks by version, not by the payload order; drafts and foreign tags skip.</summary>
    [Fact]
    public async Task TheRanking_PicksTheNewestParsableRelease_AndSkipsDraftsAndForeignTags()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(
                Release("0.1.0-alpha.157", draft: true, url: "https://example.com/draft"),
                Release("banana-tag"),
                Release("v0.1.0-alpha.159"),
                Release("0.1.0-alpha.158")));

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);
        var events = new List<Notification>();
        using var eventsSubscription = harness.Center.Events.Subscribe(events.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;

        verdicts[1].Should().NotBeNull().And.BeEquivalentTo(new UpdateRelease(
            "0.1.0-alpha.159",
            "https://github.com/zettatrade/zettatrade/releases/tag/v0.1.0-alpha.159"),
            "the newest parsable published release ranks (the draft and the foreign tag never rank)");
        events.Should().ContainSingle().Which.VersionLabel.Should().Be("0.1.0-alpha.159");
    }

    #endregion

    #region Tests: the degraded states

    /// <summary>An unreachable endpoint degrades to a logged warning: the verdict stream stays silent, the standing verdict stays.</summary>
    [Fact]
    public async Task AnUnreachableEndpoint_EmitsNothing_AndTheStandingVerdictStays()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159")));

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;
        verdicts.Should().HaveCount(2, "the boot check completed and announced");

        harness.Handler.StatusCode = HttpStatusCode.InternalServerError;
        await harness.Service.CheckNowAsync();

        verdicts.Should().HaveCount(2, "the failed check emits NOTHING — the standing verdict stays, the chip never flaps");
        harness.Handler.Requests.Should().Be(2, "the failed check still made its request (the network was reached)");
    }

    /// <summary>
    /// The feed's no-release 404 is the honest no-update state (T-230):
    /// the check completes with the empty verdict — not a degraded
    /// check, no warning family, nothing announced.
    /// </summary>
    [Fact]
    public async Task TheNoRelease404_CompletesAsTheHonestEmptyVerdict()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159")));
        harness.Handler.StatusCode = HttpStatusCode.NotFound;

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);
        var events = new List<Notification>();
        using var eventsSubscription = harness.Center.Events.Subscribe(events.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;
        await harness.Service.CheckNowAsync();

        verdicts.Should().HaveCount(3, "the replay + the two COMPLETED checks — the 404 never degrades them");
        verdicts[1].Should().BeNull("the no-release feed is the honest empty verdict, not a failed check");
        verdicts[2].Should().BeNull("the repeat check answers the same honest empty verdict");
        harness.Service.Latest.Should().BeNull();
        events.Should().BeEmpty("nothing published, nothing announced");
        harness.Handler.Requests.Should().Be(2, "the 404 check still asked the feed (and will again — an honest ask)");
    }

    /// <summary>A malformed payload degrades the same way: a warning, no emission.</summary>
    [Fact]
    public async Task AMalformedPayload_EmitsNothing()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: "<html>rate limited</html>");

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;

        verdicts.Should().HaveCount(1, "a non-array payload is a degraded check — only the replay, never a fabricated verdict");
        harness.Service.Latest.Should().BeNull();
    }

    #endregion

    #region Tests: the off switch

    /// <summary>The off switch silences the check entirely: the boot check and the manual ask both stay home.</summary>
    [Fact]
    public async Task TheOffSwitch_SilencesTheCheckEntirely()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159")),
            seedTheOffSwitch: true);

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;
        await harness.Service.CheckNowAsync();

        harness.Handler.Requests.Should().Be(0, "a silenced check makes no request — neither the boot check nor the manual ask");
        verdicts.Should().HaveCount(1, "only the stream's null replay — a silenced check emits nothing");
        harness.Service.IsEnabled.Should().BeFalse();
    }

    /// <summary>A live flip re-arms the check without a restart: the next manual ask asks.</summary>
    [Fact]
    public async Task ALiveFlip_RearsTheCheck_WithoutARestart()
    {
        var harness = await CreateHarnessAsync(
            currentVersion: "0.1.0-alpha.158",
            payloadJson: ReleasesJson(Release("v0.1.0-alpha.159")),
            seedTheOffSwitch: true);

        var verdicts = new List<UpdateRelease?>();
        using var subscription = harness.Service.Results.Subscribe(verdicts.Add);

        await harness.Service.InitializeAsync();
        await harness.Service.FirstCheckForTests!;
        harness.Handler.Requests.Should().Be(0, "the off state never requests");

        await harness.Settings.SetAsync(AppSettingsKeys.Section, AppSettingsKeys.UpdateCheck, true);
        harness.Service.IsEnabled.Should().BeTrue("the change stream re-applies the flip live");

        await harness.Service.CheckNowAsync();

        harness.Handler.Requests.Should().Be(1, "the re-armed check asks again");
        verdicts.Should().HaveCount(2);
        verdicts[1].Should().NotBeNull("the completed check emits its verdict");
    }

    #endregion

    #region Harness

    /// <summary>One releases stub + its checker (fresh, not initialized — the facts drive init themselves).</summary>
    private static async Task<Harness> CreateHarnessAsync(
        string currentVersion,
        string payloadJson,
        bool seedTheOffSwitch = false)
    {
        var settings = new FakeSettings();
        if (seedTheOffSwitch)
        {
            await settings.SetAsync(AppSettingsKeys.Section, AppSettingsKeys.UpdateCheck, false);
        }

        var handler = new StubReleasesHandler { PayloadJson = payloadJson };
        var center = new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance);
        var service = new UpdateCheckService(
            settings,
            new StubHttpClientFactory(handler),
            center,
            NullLogger<UpdateCheckService>.Instance,
            currentVersion);

        return new Harness(settings, handler, center, service);
    }

    private sealed record Harness(
        FakeSettings Settings,
        StubReleasesHandler Handler,
        NotificationCenter Center,
        UpdateCheckService Service);

    /// <summary>One release object in the endpoint's payload shape.</summary>
    private static string Release(string tag, string? url = null, bool draft = false)
    {
        var urlValue = url ?? $"https://github.com/zettatrade/zettatrade/releases/tag/{tag}";
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":true,"html_url":"{{urlValue}}","name":"{{tag}}"}""";
    }

    /// <summary>The releases list payload.</summary>
    private static string ReleasesJson(params string[] releases) =>
        "[" + string.Join(",", releases) + "]";

    /// <summary>The canned endpoint: counts every request, answers the payload (or the failure mode) it carries.</summary>
    private sealed class StubReleasesHandler : HttpMessageHandler
    {
        public int Requests;

        public string? PayloadJson { get; set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var payload = PayloadJson ?? "{\"message\":\"degraded\"}";
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>The factory double: every client rides the one stub (the named-client seam stays the production shape).</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(UpdateCheckService.HttpClientName, "the checker rides its named client");
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>
    /// An in-memory reactive settings double (the NotificationCenterTests
    /// shape in miniature): the reads answer the store, the writes land in
    /// it and announce on the change stream.
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
