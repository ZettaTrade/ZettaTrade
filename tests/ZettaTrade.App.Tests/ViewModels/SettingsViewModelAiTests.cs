namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The settings window's AI chat section (T-207): the provider pick and the
/// two providers' pieces draft like every other selection (seeded from the
/// persisted state on every open, unsaved picks discarded, only a real
/// difference applies on Save), a garbage stored pick drafts the default
/// with a logged warning, and the API key is the one immediate field — it
/// commits to the secret store per keystroke (typed stores, cleared
/// removes), the window-open re-seed never deletes the stored key, and a
/// store-less construction stays memory-only.
/// </summary>
public sealed class SettingsViewModelAiTests
{
    #region Seeding

    /// <summary>
    /// A fresh store seeds the defaults: the pick rides Ollama, the Ollama
    /// pieces show the shipped local defaults, and the OpenAI-compatible
    /// model stays empty (no model is a send-time error, never a guessed
    /// name).
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_TheDefaultsSeedTheDrafts()
    {
        var kit = CreateKit();

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AiProviderIndex.Should().Be(0);
        kit.ViewModel.IsAiOllama.Should().BeTrue();
        kit.ViewModel.IsAiOpenAiCompatible.Should().BeFalse();
        kit.ViewModel.OllamaUrlDraft.Should().Be(AiSettingsKeys.DefaultOllamaUrl);
        kit.ViewModel.OllamaModelDraft.Should().Be(AiSettingsKeys.DefaultModel);
        kit.ViewModel.OpenAiBaseUrlDraft.Should().Be(AiSettingsKeys.DefaultOpenAiCompatibleBaseUrl);
        kit.ViewModel.OpenAiModelDraft.Should().BeEmpty();
    }

    /// <summary>
    /// The stored pieces seed the drafts (trimmed): the pick lands on its
    /// provider and every piece shows exactly what the store holds.
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_TheStoredPiecesSeedTheDrafts()
    {
        var kit = CreateKit(
            storedProvider: "OpenAiCompatible",
            storedOllamaUrl: " http://localhost:9999 ",
            storedOllamaModel: " qwen ",
            storedOpenAiBaseUrl: " http://localhost:8080/v1 ",
            storedOpenAiModel: " mistral-7b ");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AiProviderIndex.Should().Be(1);
        kit.ViewModel.IsAiOpenAiCompatible.Should().BeTrue();
        kit.ViewModel.OllamaUrlDraft.Should().Be("http://localhost:9999");
        kit.ViewModel.OllamaModelDraft.Should().Be("qwen");
        kit.ViewModel.OpenAiBaseUrlDraft.Should().Be("http://localhost:8080/v1");
        kit.ViewModel.OpenAiModelDraft.Should().Be("mistral-7b");
    }

    /// <summary>
    /// A garbage stored pick drafts the default with a logged warning: the
    /// UI never shows a broken pick, and the honest refusal stays at the
    /// router's send seam.
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_AGarbagePick_DraftsTheDefaultAndLogsTheWarning()
    {
        var logger = new RecordingLogger();
        var kit = CreateKit(storedProvider: "Mistral", logger: logger);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AiProviderIndex.Should().Be(0);
        logger.AllText.Should().Contain("Mistral", "the honest warning names the stored garbage");
    }

    /// <summary>
    /// An unsaved provider pick dies at the next open (the same draft
    /// contract every other selection keeps).
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit();

        kit.ViewModel.AiProviderIndex = 1;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AiProviderIndex.Should().Be(0);
    }

    #endregion

    #region Saving

    /// <summary>
    /// Saving the changed drafts persists exactly the real difference: the
    /// provider pick and the four pieces, each under its own key, each in
    /// its stored shape.
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheChangedDrafts_PersistTheRealDifference()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AiProviderIndex = 1;
        kit.ViewModel.OllamaUrlDraft = "http://localhost:9999 ";
        kit.ViewModel.OllamaModelDraft = " qwen";
        kit.ViewModel.OpenAiBaseUrlDraft = " http://localhost:8080/v1";
        kit.ViewModel.OpenAiModelDraft = "mistral-7b";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.Section,
            AiSettingsKeys.Provider,
            "OpenAiCompatible");
        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.Section,
            AiSettingsKeys.OllamaUrl,
            "http://localhost:9999");
        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.Section,
            AiSettingsKeys.Model,
            "qwen");
        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.OpenAiCompatibleSection,
            AiSettingsKeys.OpenAiCompatibleBaseUrl,
            "http://localhost:8080/v1");
        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.OpenAiCompatibleSection,
            AiSettingsKeys.OpenAiCompatibleModel,
            "mistral-7b");
    }

    /// <summary>
    /// An untouched save stays silent: no fabricated writes — the defaults
    /// live in the read-time fallbacks, not in the store.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnUntouchedSave_StaysSilent()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// An emptied OpenAI-compatible model draft saves absence: the empty box
    /// is the honest no-model state (the send refuses), never a rewritten
    /// guess.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnEmptiedOpenAiModelDraft_SavesAbsence()
    {
        var kit = CreateKit(storedOpenAiModel: "mistral-7b");
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.OpenAiModelDraft = string.Empty;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            AiSettingsKeys.OpenAiCompatibleSection,
            AiSettingsKeys.OpenAiCompatibleModel,
            string.Empty);
    }

    #endregion

    #region The Immediate Key

    /// <summary>
    /// A typed key commits to the secret store immediately (per keystroke —
    /// here, one assignment): the secret's only home is the store, there is
    /// no save step to hold it.
    /// </summary>
    [Fact]
    public async Task TheTypedApiKey_CommitsImmediately()
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

        kit.ViewModel.OpenAiApiKeyDraft = "sk-typed-key";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("sk-typed-key");
        await secrets.DidNotReceive().RemoveAsync(
            AiSettingsKeys.OpenAiCompatibleApiKeySecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A cleared box removes the stored key immediately: clearing is a real
    /// intent, and the request then travels unauthenticated.
    /// </summary>
    [Fact]
    public async Task TheClearedApiKey_RemovesImmediately()
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

        kit.ViewModel.OpenAiApiKeyDraft = "sk-typed-key";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.OpenAiApiKeyDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey);
        await secrets.Received(1).RemoveAsync(
            AiSettingsKeys.OpenAiCompatibleApiKeySecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The window-open re-seed clears the key box but NEVER deletes the
    /// stored key: a re-seed must not commit — only the user's own clear is
    /// a deletion.
    /// </summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredKey()
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

        kit.ViewModel.OpenAiApiKeyDraft = "sk-typed-key";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.OpenAiApiKeyDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            AiSettingsKeys.OpenAiCompatibleApiKeySecretKey,
            "sk-typed-key",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A store-less construction stays memory-only: the key draft holds
    /// (the UI works) and commits nothing.
    /// </summary>
    [Fact]
    public async Task AStorelessConstruction_TheKeyDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.OpenAiApiKeyDraft = "sk-typed-key";

        kit.ViewModel.OpenAiApiKeyDraft.Should().Be("sk-typed-key");
    }

    #endregion

    #region Factories

    /// <summary>
    /// One wired AI-tab kit: a per-key settings substitute (every string
    /// read echoes the passed default first — the real store's fresh-install
    /// contract — and the key-specific stored values ride last), the demo
    /// exchange facade, single-row managers and the optional secret store.
    /// </summary>
    private static AiKit CreateKit(
        string storedProvider = "",
        string storedOllamaUrl = "",
        string storedOllamaModel = "",
        string storedOpenAiBaseUrl = "",
        string storedOpenAiModel = "",
        ISecretStore? secrets = null,
        ILogger<SettingsViewModel>? logger = null)
    {
        var settings = Substitute.For<ISettingsService>();
        // The fresh-install contract mirrored: an unconfigured read echoes
        // the passed default (the caches land on the shipped defaults).
        settings
            .GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => call.ArgAt<string>(2));
        settings
            .GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        // The AI keys' stored values ride LAST: the last matching setup
        // wins. The matchers pair the section with the key (the "Model"
        // key lives in BOTH sections), and a blank stored value echoes the
        // passed default — the real store's absent-key contract (the cache
        // lands on the shipped default, an untouched save stays silent).
        settings
            .GetAsync<string>(
                Arg.Is<string>(section => section == AiSettingsKeys.Section),
                Arg.Is<string>(key => key == AiSettingsKeys.Provider),
                Arg.Any<string>())
            .Returns(info => string.IsNullOrWhiteSpace(storedProvider)
                ? info.ArgAt<string>(2)
                : storedProvider);
        settings
            .GetAsync<string>(
                Arg.Is<string>(section => section == AiSettingsKeys.Section),
                Arg.Is<string>(key => key == AiSettingsKeys.OllamaUrl),
                Arg.Any<string>())
            .Returns(info => string.IsNullOrWhiteSpace(storedOllamaUrl)
                ? info.ArgAt<string>(2)
                : storedOllamaUrl);
        settings
            .GetAsync<string>(
                Arg.Is<string>(section => section == AiSettingsKeys.Section),
                Arg.Is<string>(key => key == AiSettingsKeys.Model),
                Arg.Any<string>())
            .Returns(info => string.IsNullOrWhiteSpace(storedOllamaModel)
                ? info.ArgAt<string>(2)
                : storedOllamaModel);
        settings
            .GetAsync<string>(
                Arg.Is<string>(section => section == AiSettingsKeys.OpenAiCompatibleSection),
                Arg.Is<string>(key => key == AiSettingsKeys.OpenAiCompatibleBaseUrl),
                Arg.Any<string>())
            .Returns(info => string.IsNullOrWhiteSpace(storedOpenAiBaseUrl)
                ? info.ArgAt<string>(2)
                : storedOpenAiBaseUrl);
        settings
            .GetAsync<string>(
                Arg.Is<string>(section => section == AiSettingsKeys.OpenAiCompatibleSection),
                Arg.Is<string>(key => key == AiSettingsKeys.OpenAiCompatibleModel),
                Arg.Any<string>())
            .Returns(info => string.IsNullOrWhiteSpace(storedOpenAiModel)
                ? info.ArgAt<string>(2)
                : storedOpenAiModel);
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
            logger ?? NullLogger<SettingsViewModel>.Instance,
            secrets: secrets);

        return new AiKit(viewModel, settings, secrets);
    }

    /// <summary>A capturing logger: every formatted entry and every exception text lands in one searchable pool.</summary>
    private sealed class RecordingLogger : ILogger<SettingsViewModel>
    {
        public List<string> Entries { get; } = [];

        public string AllText => string.Join(Environment.NewLine, Entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (exception is not null)
            {
                Entries.Add(exception.ToString());
            }
        }
    }

    #endregion
}

/// <summary>One wired AI-tab kit with every collaborator a test drives.</summary>
internal sealed record AiKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    ISecretStore? Secrets);
