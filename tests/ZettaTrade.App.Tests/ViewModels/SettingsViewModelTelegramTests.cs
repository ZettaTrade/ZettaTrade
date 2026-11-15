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
/// The settings window's Notifications section (T-260): the Telegram
/// sink's routing rows keep the house draft/save contract — the per-kind
/// opt-ins and the chat id draft like every other selection (seeded from
/// the persisted values on every open, unsaved picks discarded, only a
/// real difference persists) and the bot token commits to the secret store
/// immediately per keystroke (the OpenAI key's exact shape: typed stores,
/// a cleared box removes, a re-seed never commits, a store-less
/// construction stays memory-only).
/// </summary>
public sealed class SettingsViewModelTelegramTests
{
    #region Tests: the toggles

    /// <summary>The persisted opt-ins seed their toggles (the defaults off keep them unchecked).</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects)
    {
        var kit = CreateKit(persistedFills: persistedFills, persistedCancels: persistedCancels, persistedRejects: persistedRejects);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsTelegramFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsTelegramCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsTelegramRejectsEnabled.Should().Be(persistedRejects);
    }

    /// <summary>An unsaved toggle pick dies at the next open (the same draft contract every other selection keeps).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit(persistedFills: false);

        kit.ViewModel.IsTelegramFillsEnabled = true;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsTelegramFillsEnabled.Should().BeFalse();
    }

    /// <summary>Saving a flipped toggle persists exactly that flag (the sink's own section and key).</summary>
    [Fact]
    public async Task SaveAsync_TheFlip_PersistsTheSinkFlag()
    {
        var kit = CreateKit(persistedFills: false);

        kit.ViewModel.IsTelegramFillsEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.FillsEnabledKey,
            true);
        await kit.Settings.DidNotReceive().SetAsync(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.CancelsEnabledKey,
            Arg.Any<bool>());
        await kit.Settings.DidNotReceive().SetAsync(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.RejectsEnabledKey,
            Arg.Any<bool>());
        kit.ViewModel.IsTelegramFillsEnabled.Should().BeTrue("the draft stays the applied truth after the save");
    }

    /// <summary>An unchanged set of toggles is a no-op: no settings rewrite at all (the same threshold every other row keeps).</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedChoice_StaysSilent()
    {
        var kit = CreateKit(persistedFills: false);

        kit.ViewModel.IsTelegramFillsEnabled = false;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    /// <summary>An externally flipped choice lands in the next open's draft through the seed cache.</summary>
    [Fact]
    public async Task TheExternallyFlippedChoice_FeedsTheNextOpen()
    {
        var kit = CreateKit(persistedFills: false);

        kit.Changes.OnNext(new SettingsChange(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.FillsEnabledKey,
            false,
            true));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsTelegramFillsEnabled.Should().BeTrue("the fresh cache seeds the next open's draft");
    }

    #endregion Tests: the toggles

    #region Tests: the chat id

    /// <summary>The persisted chat id seeds the draft box.</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedChatId_SeedsTheDraft()
    {
        var kit = CreateKit(persistedChatId: "424242");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.TelegramChatIdDraft.Should().Be("424242");
    }

    /// <summary>A changed chat id persists trimmed on save.</summary>
    [Fact]
    public async Task SaveAsync_TheChatIdDraft_PersistsTrimmed()
    {
        var kit = CreateKit(persistedChatId: string.Empty);

        kit.ViewModel.TelegramChatIdDraft = " 424242 ";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.ChatIdKey,
            "424242");
    }

    /// <summary>An unchanged chat id is a no-op: no settings rewrite.</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedChatId_StaysSilent()
    {
        var kit = CreateKit(persistedChatId: "424242");

        kit.ViewModel.TelegramChatIdDraft = "424242";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(
            TelegramNotifierService.SettingsSection,
            TelegramNotifierService.ChatIdKey,
            Arg.Any<string>());
    }

    #endregion Tests: the chat id

    #region Tests: the immediate token

    /// <summary>A typed token commits to the secret store immediately (per keystroke — here, one assignment): the secret's only home is the store.</summary>
    [Fact]
    public async Task TheTypedToken_CommitsImmediately()
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

        kit.ViewModel.TelegramBotTokenDraft = "123:typed-token";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("123:typed-token");
        await secrets.Received(1).SetAsync(
            TelegramNotifierService.BotTokenSecretKey,
            "123:typed-token",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            TelegramNotifierService.BotTokenSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared box removes the stored token immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedToken_RemovesImmediately()
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

        kit.ViewModel.TelegramBotTokenDraft = "123:typed-token";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.TelegramBotTokenDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(TelegramNotifierService.BotTokenSecretKey);
        await secrets.Received(1).RemoveAsync(
            TelegramNotifierService.BotTokenSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the token box but NEVER deletes the stored token: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredToken()
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

        kit.ViewModel.TelegramBotTokenDraft = "123:typed-token";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.TelegramBotTokenDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            TelegramNotifierService.BotTokenSecretKey,
            "123:typed-token",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the token draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheTokenDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.TelegramBotTokenDraft = "123:typed-token";
        await Task.Delay(200);

        kit.ViewModel.TelegramBotTokenDraft.Should().Be("123:typed-token");
    }

    #endregion Tests: the immediate token

    #region Factories

    /// <summary>
    /// One wired Notifications-section kit: a per-key settings substitute
    /// (the generic bool/string matchers answer the defaults, the Telegram
    /// keys' persisted values ride the key-specific ones — the last
    /// matching setup wins), the demo exchange facade and the host + runner
    /// pair the window reads.
    /// </summary>
    private static TelegramKit CreateKit(
        bool persistedFills = false,
        bool persistedCancels = false,
        bool persistedRejects = false,
        string persistedChatId = "",
        ISecretStore? secrets = null)
    {
        var settings = Substitute.For<ISettingsService>();
        // The generic matches answer the defaults first: the clock and the
        // other toggles' cached choices ride false, every string read echoes
        // its own default argument (the real settings service never answers
        // null for a read with a default — the substitute must not either).
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(2));
        // The Telegram opt-ins ride the key-specific setups last.
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == TelegramNotifierService.SettingsSection),
                Arg.Is<string>(key => key == TelegramNotifierService.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == TelegramNotifierService.SettingsSection),
                Arg.Is<string>(key => key == TelegramNotifierService.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == TelegramNotifierService.SettingsSection),
                Arg.Is<string>(key => key == TelegramNotifierService.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedRejects);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == TelegramNotifierService.SettingsSection),
                Arg.Is<string>(key => key == TelegramNotifierService.ChatIdKey),
                Arg.Any<string>())
            .Returns(persistedChatId);
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

        return new TelegramKit(viewModel, settings, changes, secrets);
    }

    #endregion Factories
}

/// <summary>One wired Notifications-section kit with every collaborator a test drives.</summary>
internal sealed record TelegramKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    ISecretStore? Secrets);
