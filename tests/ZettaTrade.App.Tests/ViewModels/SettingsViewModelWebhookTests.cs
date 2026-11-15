namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Notifications;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The settings window's webhook sink rows (T-261): the Discord, Slack and
/// ntfy routing rows keep the house draft/save contract — the per-kind
/// opt-ins and the ntfy topic URL like every other selection (seeded from
/// the persisted values on every open, unsaved picks discarded, only a
/// real difference persists) and the Discord/Slack webhook URLs commit to
/// the secret store immediately per keystroke (the Telegram token's exact
/// shape: typed stores, a cleared box removes, a re-seed never commits, a
/// store-less construction stays memory-only). The ntfy topic URL is
/// public by design and rides the settings file — a draft like the chat
/// id, not a secret.
/// </summary>
public sealed class SettingsViewModelWebhookTests
{
    #region Tests: the toggles

    /// <summary>The persisted Discord opt-ins seed their toggles (the defaults off keep them unchecked).</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheDiscordToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects)
    {
        var kit = CreateKit(
            persistedDiscordFills: persistedFills,
            persistedDiscordCancels: persistedCancels,
            persistedDiscordRejects: persistedRejects);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsDiscordFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsDiscordCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsDiscordRejectsEnabled.Should().Be(persistedRejects);
    }

    /// <summary>The persisted Slack opt-ins seed their toggles.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheSlackToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects)
    {
        var kit = CreateKit(
            persistedSlackFills: persistedFills,
            persistedSlackCancels: persistedCancels,
            persistedSlackRejects: persistedRejects);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsSlackFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsSlackCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsSlackRejectsEnabled.Should().Be(persistedRejects);
    }

    /// <summary>The persisted ntfy opt-ins seed their toggles.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheNtfyToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects)
    {
        var kit = CreateKit(
            persistedNtfyFills: persistedFills,
            persistedNtfyCancels: persistedCancels,
            persistedNtfyRejects: persistedRejects);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsNtfyFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsNtfyCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsNtfyRejectsEnabled.Should().Be(persistedRejects);
    }

    /// <summary>An unsaved toggle pick dies at the next open (the same draft contract every other selection keeps).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit(persistedDiscordFills: false);

        kit.ViewModel.IsDiscordFillsEnabled = true;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsDiscordFillsEnabled.Should().BeFalse();
    }

    /// <summary>Saving a flipped webhook toggle persists exactly that flag (the sink's own section and key).</summary>
    [Fact]
    public async Task SaveAsync_TheFlip_PersistsTheSinkFlag()
    {
        var kit = CreateKit(persistedDiscordFills: false);

        kit.ViewModel.IsDiscordFillsEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            DiscordSink.SettingsSection,
            DiscordSink.FillsEnabledKey,
            true);
        await kit.Settings.DidNotReceive().SetAsync(
            DiscordSink.SettingsSection,
            DiscordSink.CancelsEnabledKey,
            Arg.Any<bool>());
        await kit.Settings.DidNotReceive().SetAsync(
            DiscordSink.SettingsSection,
            DiscordSink.RejectsEnabledKey,
            Arg.Any<bool>());
        kit.ViewModel.IsDiscordFillsEnabled.Should().BeTrue("the draft stays the applied truth after the save");
    }

    /// <summary>An unchanged set of webhook rows is a no-op: no settings rewrite at all (the same threshold every other row keeps).</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedChoice_StaysSilent()
    {
        var kit = CreateKit(persistedDiscordFills: false, persistedNtfyUrl: "https://ntfy.sh/keep");

        kit.ViewModel.IsDiscordFillsEnabled = false;
        kit.ViewModel.IsSlackRejectsEnabled = false;
        kit.ViewModel.NtfyUrlDraft = "https://ntfy.sh/keep";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>());
        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    /// <summary>An externally flipped choice lands in the next open's draft through the seed cache.</summary>
    [Fact]
    public async Task TheExternallyFlippedChoice_FeedsTheNextOpen()
    {
        var kit = CreateKit(persistedNtfyFills: false);

        kit.Changes.OnNext(new SettingsChange(
            NtfySink.SettingsSection,
            NtfySink.FillsEnabledKey,
            false,
            true));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsNtfyFillsEnabled.Should().BeTrue("the fresh cache seeds the next open's draft");
    }

    #endregion Tests: the toggles

    #region Tests: the ntfy topic URL

    /// <summary>The persisted topic URL seeds the draft box (a public-by-design draft, like the chat id).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedNtfyUrl_SeedsTheDraft()
    {
        var kit = CreateKit(persistedNtfyUrl: "https://ntfy.sh/my-topic");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.NtfyUrlDraft.Should().Be("https://ntfy.sh/my-topic");
    }

    /// <summary>A changed topic URL persists trimmed on save.</summary>
    [Fact]
    public async Task SaveAsync_TheNtfyUrlDraft_PersistsTrimmed()
    {
        var kit = CreateKit(persistedNtfyUrl: string.Empty);

        kit.ViewModel.NtfyUrlDraft = " https://ntfy.sh/my-topic ";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            NtfySink.SettingsSection,
            NtfySink.TopicUrlKey,
            "https://ntfy.sh/my-topic");
    }

    /// <summary>An unchanged topic URL is a no-op: no settings rewrite.</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedNtfyUrl_StaysSilent()
    {
        var kit = CreateKit(persistedNtfyUrl: "https://ntfy.sh/my-topic");

        kit.ViewModel.NtfyUrlDraft = "https://ntfy.sh/my-topic";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(
            NtfySink.SettingsSection,
            NtfySink.TopicUrlKey,
            Arg.Any<string>());
    }

    #endregion Tests: the ntfy topic URL

    #region Tests: the immediate Discord webhook URL

    /// <summary>A typed Discord URL commits to the secret store immediately (per keystroke — here, one assignment): the URL embeds the webhook's secret material, the store is its only home.</summary>
    [Fact]
    public async Task TheTypedDiscordUrl_CommitsImmediately()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.DiscordWebhookUrlDraft = "https://discord.com/api/webhooks/123/typed";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("https://discord.com/api/webhooks/123/typed");
        await secrets.Received(1).SetAsync(
            DiscordSink.WebhookUrlSecretKey,
            "https://discord.com/api/webhooks/123/typed",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            DiscordSink.WebhookUrlSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared Discord box removes the stored URL immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedDiscordUrl_RemovesImmediately()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        secrets
            .RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                removed.TrySetResult(info.ArgAt<string>(0));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.DiscordWebhookUrlDraft = "https://discord.com/api/webhooks/123/typed";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.DiscordWebhookUrlDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(DiscordSink.WebhookUrlSecretKey);
        await secrets.Received(1).RemoveAsync(
            DiscordSink.WebhookUrlSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the Discord box but NEVER deletes the stored URL: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredDiscordUrl()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.DiscordWebhookUrlDraft = "https://discord.com/api/webhooks/123/typed";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.DiscordWebhookUrlDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            DiscordSink.WebhookUrlSecretKey,
            "https://discord.com/api/webhooks/123/typed",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the Discord draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheDiscordUrlDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.DiscordWebhookUrlDraft = "https://discord.com/api/webhooks/123/typed";
        await Task.Delay(200);

        kit.ViewModel.DiscordWebhookUrlDraft.Should().Be("https://discord.com/api/webhooks/123/typed");
    }

    #endregion Tests: the immediate Discord webhook URL

    #region Tests: the immediate Slack webhook URL

    /// <summary>A typed Slack URL commits to the secret store immediately (per keystroke — here, one assignment).</summary>
    [Fact]
    public async Task TheTypedSlackUrl_CommitsImmediately()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.SlackWebhookUrlDraft = "https://hooks.slack.com/services/T00/B00/typed";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("https://hooks.slack.com/services/T00/B00/typed");
        await secrets.Received(1).SetAsync(
            SlackSink.WebhookUrlSecretKey,
            "https://hooks.slack.com/services/T00/B00/typed",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            SlackSink.WebhookUrlSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared Slack box removes the stored URL immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedSlackUrl_RemovesImmediately()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        secrets
            .RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                removed.TrySetResult(info.ArgAt<string>(0));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.SlackWebhookUrlDraft = "https://hooks.slack.com/services/T00/B00/typed";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.SlackWebhookUrlDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(SlackSink.WebhookUrlSecretKey);
        await secrets.Received(1).RemoveAsync(
            SlackSink.WebhookUrlSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the Slack box but NEVER deletes the stored URL: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredSlackUrl()
    {
        var stored = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(info =>
            {
                stored.TrySetResult(info.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        var kit = CreateKit(secrets: secrets);

        kit.ViewModel.SlackWebhookUrlDraft = "https://hooks.slack.com/services/T00/B00/typed";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.SlackWebhookUrlDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            SlackSink.WebhookUrlSecretKey,
            "https://hooks.slack.com/services/T00/B00/typed",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the Slack draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheSlackUrlDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.SlackWebhookUrlDraft = "https://hooks.slack.com/services/T00/B00/typed";
        await Task.Delay(200);

        kit.ViewModel.SlackWebhookUrlDraft.Should().Be("https://hooks.slack.com/services/T00/B00/typed");
    }

    #endregion Tests: the immediate Slack webhook URL

    #region Factories

    /// <summary>
    /// One wired Notifications-section kit: a per-key settings substitute
    /// (the generic bool/string matchers answer the defaults, the webhook
    /// rows' persisted values ride the key-specific ones — the last
    /// matching setup wins) and the same collaborator set the window reads.
    /// </summary>
    private static WebhookKit CreateKit(
        bool persistedDiscordFills = false,
        bool persistedDiscordCancels = false,
        bool persistedDiscordRejects = false,
        bool persistedSlackFills = false,
        bool persistedSlackCancels = false,
        bool persistedSlackRejects = false,
        bool persistedNtfyFills = false,
        bool persistedNtfyCancels = false,
        bool persistedNtfyRejects = false,
        string persistedNtfyUrl = "",
        ISecretStore? secrets = null)
    {
        var settings = Substitute.For<ISettingsService>();
        // The generic matches answer the defaults first: the untouched
        // rows' cached choices ride false, every string read echoes its own
        // default argument (the real settings service never answers null
        // for a read with a default — the substitute must not either).
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(2));
        // The webhook rows ride the key-specific setups last.
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == DiscordSink.SettingsSection),
                Arg.Is<string>(key => key == DiscordSink.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedDiscordFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == DiscordSink.SettingsSection),
                Arg.Is<string>(key => key == DiscordSink.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedDiscordCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == DiscordSink.SettingsSection),
                Arg.Is<string>(key => key == DiscordSink.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedDiscordRejects);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == SlackSink.SettingsSection),
                Arg.Is<string>(key => key == SlackSink.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedSlackFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == SlackSink.SettingsSection),
                Arg.Is<string>(key => key == SlackSink.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedSlackCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == SlackSink.SettingsSection),
                Arg.Is<string>(key => key == SlackSink.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedSlackRejects);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == NtfySink.SettingsSection),
                Arg.Is<string>(key => key == NtfySink.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedNtfyFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == NtfySink.SettingsSection),
                Arg.Is<string>(key => key == NtfySink.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedNtfyCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == NtfySink.SettingsSection),
                Arg.Is<string>(key => key == NtfySink.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedNtfyRejects);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == NtfySink.SettingsSection),
                Arg.Is<string>(key => key == NtfySink.TopicUrlKey),
                Arg.Any<string>())
            .Returns(persistedNtfyUrl);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("Dark");
        themeManager.AvailableThemes.Returns([new ThemeInfo("dark", "Dark", true, [])]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns([new LanguageInfo("en", "English", "English", true)]);
        var shellUtils = Substitute.For<IShellUtils>();

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);
        var symbolList = new SymbolListProvider(
            activeExchanges,
            NullLogger<SymbolListProvider>.Instance);

        var host = Substitute.For<IMcpServerHost>();
        var runner = new McpHostService(
            settings,
            host,
            NullLogger<McpHostService>.Instance);

        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(["SMA"]);

        var manager = Substitute.For<IMcpClientManager>();
        manager.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            shellUtils,
            runner,
            activeExchanges,
            symbolList,
            catalog,
            manager,
            NullLogger<SettingsViewModel>.Instance,
            secrets: secrets);

        return new WebhookKit(viewModel, settings, changes, secrets);
    }

    #endregion Factories
}

/// <summary>One wired Notifications-section kit with every collaborator a test drives.</summary>
internal sealed record WebhookKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    ISecretStore? Secrets);
