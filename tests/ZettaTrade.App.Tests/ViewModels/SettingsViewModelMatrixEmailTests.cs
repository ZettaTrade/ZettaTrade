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
/// The settings window's Matrix and email sink rows (T-262): the two
/// transports keep the house draft/save contract — the per-kind opt-ins
/// and the public-by-design addresses (the Matrix homeserver URL and room
/// id, the SMTP host, port and envelope) like every other selection
/// (seeded from the persisted values on every open, unsaved picks
/// discarded, only a real difference persists, a trimmed draft applies);
/// the Matrix access token and the SMTP credentials commit to the secret
/// store immediately per keystroke (the webhook URLs' exact shape: typed
/// stores, a cleared box removes, a re-seed never commits, a store-less
/// construction stays memory-only). The STARTTLS pick ships ON — the
/// credentials must not ride the wire in the clear unconfigured.
/// </summary>
public sealed class SettingsViewModelMatrixEmailTests
{
    #region Tests: the toggles

    /// <summary>The persisted Matrix opt-ins seed their toggles (the defaults off keep them unchecked).</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheMatrixToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects)
    {
        var kit = CreateKit(
            persistedMatrixFills: persistedFills,
            persistedMatrixCancels: persistedCancels,
            persistedMatrixRejects: persistedRejects);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsMatrixFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsMatrixCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsMatrixRejectsEnabled.Should().Be(persistedRejects);
    }

    /// <summary>The persisted email opt-ins seed their toggles (the STARTTLS pick among them).</summary>
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public async Task ResetSelectionsAsync_ThePersistedChoices_SeedTheEmailToggles(
        bool persistedFills,
        bool persistedCancels,
        bool persistedRejects,
        bool persistedTls)
    {
        var kit = CreateKit(
            persistedEmailFills: persistedFills,
            persistedEmailCancels: persistedCancels,
            persistedEmailRejects: persistedRejects,
            persistedEmailTls: persistedTls);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsEmailFillsEnabled.Should().Be(persistedFills);
        kit.ViewModel.IsEmailCancelsEnabled.Should().Be(persistedCancels);
        kit.ViewModel.IsEmailRejectsEnabled.Should().Be(persistedRejects);
        kit.ViewModel.IsEmailTlsEnabled.Should().Be(persistedTls);
    }

    /// <summary>An unsaved toggle pick dies at the next open (the same draft contract every other selection keeps).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit(persistedMatrixFills: false);

        kit.ViewModel.IsMatrixFillsEnabled = true;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsMatrixFillsEnabled.Should().BeFalse();
    }

    /// <summary>Saving a flipped Matrix toggle persists exactly that flag (the sink's own section and key).</summary>
    [Fact]
    public async Task SaveAsync_TheMatrixFlip_PersistsTheSinkFlag()
    {
        var kit = CreateKit(persistedMatrixFills: false);

        kit.ViewModel.IsMatrixFillsEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.FillsEnabledKey,
            true);
        await kit.Settings.DidNotReceive().SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.CancelsEnabledKey,
            Arg.Any<bool>());
        await kit.Settings.DidNotReceive().SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.RejectsEnabledKey,
            Arg.Any<bool>());
        kit.ViewModel.IsMatrixFillsEnabled.Should().BeTrue("the draft stays the applied truth after the save");
    }

    /// <summary>Saving the STARTTLS pick persists exactly that pick.</summary>
    [Fact]
    public async Task SaveAsync_TheTlsFlip_PersistsThePick()
    {
        var kit = CreateKit(persistedEmailTls: false);

        kit.ViewModel.IsEmailTlsEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            EmailSink.SettingsSection,
            EmailSink.EnableSslKey,
            true);
        kit.ViewModel.IsEmailTlsEnabled.Should().BeTrue("the draft stays the applied truth after the save");
    }

    /// <summary>An unchanged set of Matrix and email rows is a no-op: no settings rewrite at all (the same threshold every other row keeps).</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedChoice_StaysSilent()
    {
        var kit = CreateKit(persistedMatrixHomeserver: "https://matrix.example.org", persistedEmailHost: "smtp.example.org");

        kit.ViewModel.IsMatrixFillsEnabled = false;
        kit.ViewModel.IsEmailTlsEnabled = false;
        kit.ViewModel.MatrixHomeserverUrlDraft = "https://matrix.example.org";
        kit.ViewModel.EmailHostDraft = "smtp.example.org";
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
        var kit = CreateKit(persistedMatrixFills: false);

        kit.Changes.OnNext(new SettingsChange(
            MatrixSink.SettingsSection,
            MatrixSink.FillsEnabledKey,
            false,
            true));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsMatrixFillsEnabled.Should().BeTrue("the fresh cache seeds the next open's draft");
    }

    #endregion Tests: the toggles

    #region Tests: the public-by-design address rows

    /// <summary>The persisted homeserver URL seeds the draft box (a public-by-design draft, like the ntfy topic URL).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedHomeserverUrl_SeedsTheDraft()
    {
        var kit = CreateKit(persistedMatrixHomeserver: "https://matrix.example.org");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.MatrixHomeserverUrlDraft.Should().Be("https://matrix.example.org");
    }

    /// <summary>A changed homeserver URL persists trimmed on save.</summary>
    [Fact]
    public async Task SaveAsync_TheHomeserverDraft_PersistsTrimmed()
    {
        var kit = CreateKit(persistedMatrixHomeserver: string.Empty);

        kit.ViewModel.MatrixHomeserverUrlDraft = " https://matrix.example.org ";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.HomeserverUrlKey,
            "https://matrix.example.org");
    }

    /// <summary>An unchanged homeserver URL is a no-op: no settings rewrite.</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedHomeserverUrl_StaysSilent()
    {
        var kit = CreateKit(persistedMatrixHomeserver: "https://matrix.example.org");

        kit.ViewModel.MatrixHomeserverUrlDraft = "https://matrix.example.org";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.HomeserverUrlKey,
            Arg.Any<string>());
    }

    /// <summary>The persisted room id seeds the draft box (the homeserver draft's exact shape).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedRoomId_SeedsTheDraft()
    {
        var kit = CreateKit(persistedMatrixRoom: "!room:example.org");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.MatrixRoomIdDraft.Should().Be("!room:example.org");
    }

    /// <summary>A changed room id persists trimmed on save.</summary>
    [Fact]
    public async Task SaveAsync_TheRoomDraft_PersistsTrimmed()
    {
        var kit = CreateKit(persistedMatrixRoom: string.Empty);

        kit.ViewModel.MatrixRoomIdDraft = " !room:example.org ";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.RoomIdKey,
            "!room:example.org");
    }

    /// <summary>An unchanged room id is a no-op: no settings rewrite.</summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedRoomId_StaysSilent()
    {
        var kit = CreateKit(persistedMatrixRoom: "!room:example.org");

        kit.ViewModel.MatrixRoomIdDraft = "!room:example.org";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(
            MatrixSink.SettingsSection,
            MatrixSink.RoomIdKey,
            Arg.Any<string>());
    }

    /// <summary>The persisted SMTP rows seed their draft boxes (the host, the port, the envelope).</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedSmtpRows_SeedTheDrafts()
    {
        var kit = CreateKit(
            persistedEmailHost: "smtp.example.org",
            persistedEmailPort: "587",
            persistedEmailFrom: "sender@example.org",
            persistedEmailTo: "recipient@example.org");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.EmailHostDraft.Should().Be("smtp.example.org");
        kit.ViewModel.EmailPortDraft.Should().Be("587");
        kit.ViewModel.EmailFromDraft.Should().Be("sender@example.org");
        kit.ViewModel.EmailToDraft.Should().Be("recipient@example.org");
    }

    /// <summary>The changed SMTP rows persist trimmed on save (each row its own key).</summary>
    [Fact]
    public async Task SaveAsync_TheSmtpDrafts_PersistTrimmed()
    {
        var kit = CreateKit(
            persistedEmailHost: string.Empty,
            persistedEmailPort: string.Empty,
            persistedEmailFrom: string.Empty,
            persistedEmailTo: string.Empty);

        kit.ViewModel.EmailHostDraft = " smtp.example.org ";
        kit.ViewModel.EmailPortDraft = " 587 ";
        kit.ViewModel.EmailFromDraft = " sender@example.org ";
        kit.ViewModel.EmailToDraft = " recipient@example.org ";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(EmailSink.SettingsSection, EmailSink.HostKey, "smtp.example.org");
        await kit.Settings.Received(1).SetAsync(EmailSink.SettingsSection, EmailSink.PortKey, "587");
        await kit.Settings.Received(1).SetAsync(EmailSink.SettingsSection, EmailSink.FromKey, "sender@example.org");
        await kit.Settings.Received(1).SetAsync(EmailSink.SettingsSection, EmailSink.ToKey, "recipient@example.org");
    }

    /// <summary>The unchanged SMTP rows are a no-op: no settings rewrite.</summary>
    [Fact]
    public async Task SaveAsync_UnchangedSmtpRows_StaysSilent()
    {
        var kit = CreateKit(
            persistedEmailHost: "smtp.example.org",
            persistedEmailPort: "587",
            persistedEmailFrom: "sender@example.org",
            persistedEmailTo: "recipient@example.org");

        kit.ViewModel.EmailHostDraft = "smtp.example.org";
        kit.ViewModel.EmailPortDraft = "587";
        kit.ViewModel.EmailFromDraft = "sender@example.org";
        kit.ViewModel.EmailToDraft = "recipient@example.org";
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(EmailSink.SettingsSection, EmailSink.HostKey, Arg.Any<string>());
        await kit.Settings.DidNotReceive().SetAsync(EmailSink.SettingsSection, EmailSink.PortKey, Arg.Any<string>());
        await kit.Settings.DidNotReceive().SetAsync(EmailSink.SettingsSection, EmailSink.FromKey, Arg.Any<string>());
        await kit.Settings.DidNotReceive().SetAsync(EmailSink.SettingsSection, EmailSink.ToKey, Arg.Any<string>());
    }

    #endregion Tests: the public-by-design address rows

    #region Tests: the immediate Matrix access token

    /// <summary>A typed Matrix token commits to the secret store immediately (per keystroke — here, one assignment): the token embeds the channel's secret material, the store is its only home.</summary>
    [Fact]
    public async Task TheTypedMatrixToken_CommitsImmediately()
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

        kit.ViewModel.MatrixAccessTokenDraft = "syt.typed.token.0";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("syt.typed.token.0");
        await secrets.Received(1).SetAsync(
            MatrixSink.AccessTokenSecretKey,
            "syt.typed.token.0",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            MatrixSink.AccessTokenSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared Matrix box removes the stored token immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedMatrixToken_RemovesImmediately()
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

        kit.ViewModel.MatrixAccessTokenDraft = "syt.typed.token.0";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.MatrixAccessTokenDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(MatrixSink.AccessTokenSecretKey);
        await secrets.Received(1).RemoveAsync(
            MatrixSink.AccessTokenSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the Matrix box but NEVER deletes the stored token: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredMatrixToken()
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

        kit.ViewModel.MatrixAccessTokenDraft = "syt.typed.token.0";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.MatrixAccessTokenDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            MatrixSink.AccessTokenSecretKey,
            "syt.typed.token.0",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the Matrix draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheMatrixTokenDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.MatrixAccessTokenDraft = "syt.typed.token.0";
        await Task.Delay(200);

        kit.ViewModel.MatrixAccessTokenDraft.Should().Be("syt.typed.token.0");
    }

    #endregion Tests: the immediate Matrix access token

    #region Tests: the immediate SMTP credentials

    /// <summary>A typed SMTP username commits to the secret store immediately (per keystroke — here, one assignment).</summary>
    [Fact]
    public async Task TheTypedSmtpUsername_CommitsImmediately()
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

        kit.ViewModel.EmailUsernameDraft = "trade-bot@example.org";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("trade-bot@example.org");
        await secrets.Received(1).SetAsync(
            EmailSink.UsernameSecretKey,
            "trade-bot@example.org",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            EmailSink.UsernameSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared SMTP username box removes the stored credential immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedSmtpUsername_RemovesImmediately()
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

        kit.ViewModel.EmailUsernameDraft = "trade-bot@example.org";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.EmailUsernameDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(EmailSink.UsernameSecretKey);
        await secrets.Received(1).RemoveAsync(
            EmailSink.UsernameSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the username box but NEVER deletes the stored credential: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredSmtpUsername()
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

        kit.ViewModel.EmailUsernameDraft = "trade-bot@example.org";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.EmailUsernameDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            EmailSink.UsernameSecretKey,
            "trade-bot@example.org",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the username draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheSmtpUsernameDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.EmailUsernameDraft = "trade-bot@example.org";
        await Task.Delay(200);

        kit.ViewModel.EmailUsernameDraft.Should().Be("trade-bot@example.org");
    }

    /// <summary>A typed SMTP password commits to the secret store immediately (per keystroke — here, one assignment).</summary>
    [Fact]
    public async Task TheTypedSmtpPassword_CommitsImmediately()
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

        kit.ViewModel.EmailPasswordDraft = "typed.app.pw.0";

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be("typed.app.pw.0");
        await secrets.Received(1).SetAsync(
            EmailSink.PasswordSecretKey,
            "typed.app.pw.0",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            EmailSink.PasswordSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>A cleared SMTP password box removes the stored credential immediately: clearing is a real intent.</summary>
    [Fact]
    public async Task TheClearedSmtpPassword_RemovesImmediately()
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

        kit.ViewModel.EmailPasswordDraft = "typed.app.pw.0";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        kit.ViewModel.EmailPasswordDraft = string.Empty;

        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be(EmailSink.PasswordSecretKey);
        await secrets.Received(1).RemoveAsync(
            EmailSink.PasswordSecretKey,
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window-open re-seed clears the password box but NEVER deletes the stored credential: a re-seed must not commit.</summary>
    [Fact]
    public async Task TheWindowOpenReSeed_NeverDeletesTheStoredSmtpPassword()
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

        kit.ViewModel.EmailPasswordDraft = "typed.app.pw.0";
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.EmailPasswordDraft.Should().BeEmpty("the secret never echoes");
        await secrets.Received(1).SetAsync(
            EmailSink.PasswordSecretKey,
            "typed.app.pw.0",
            Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().RemoveAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A store-less construction stays memory-only: the password draft holds (the UI works) and commits nothing.</summary>
    [Fact]
    public async Task AStorelessConstruction_TheSmtpPasswordDraftStaysMemoryOnly()
    {
        var kit = CreateKit(secrets: null);

        kit.ViewModel.EmailPasswordDraft = "typed.app.pw.0";
        await Task.Delay(200);

        kit.ViewModel.EmailPasswordDraft.Should().Be("typed.app.pw.0");
    }

    #endregion Tests: the immediate SMTP credentials

    #region Factories

    /// <summary>
    /// One wired Notifications-section kit: a per-key settings substitute
    /// (the generic bool/string matchers answer the defaults, the Matrix
    /// and email rows' persisted values ride the key-specific ones — the
    /// last matching setup wins) and the same collaborator set the window
    /// reads.
    /// </summary>
    private static MatrixEmailKit CreateKit(
        bool persistedMatrixFills = false,
        bool persistedMatrixCancels = false,
        bool persistedMatrixRejects = false,
        string persistedMatrixHomeserver = "",
        string persistedMatrixRoom = "",
        bool persistedEmailFills = false,
        bool persistedEmailCancels = false,
        bool persistedEmailRejects = false,
        bool persistedEmailTls = false,
        string persistedEmailHost = "",
        string persistedEmailPort = "",
        string persistedEmailFrom = "",
        string persistedEmailTo = "",
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
        // The Matrix and email rows ride the key-specific setups last.
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == MatrixSink.SettingsSection),
                Arg.Is<string>(key => key == MatrixSink.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedMatrixFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == MatrixSink.SettingsSection),
                Arg.Is<string>(key => key == MatrixSink.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedMatrixCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == MatrixSink.SettingsSection),
                Arg.Is<string>(key => key == MatrixSink.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedMatrixRejects);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == MatrixSink.SettingsSection),
                Arg.Is<string>(key => key == MatrixSink.HomeserverUrlKey),
                Arg.Any<string>())
            .Returns(persistedMatrixHomeserver);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == MatrixSink.SettingsSection),
                Arg.Is<string>(key => key == MatrixSink.RoomIdKey),
                Arg.Any<string>())
            .Returns(persistedMatrixRoom);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.FillsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedEmailFills);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.CancelsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedEmailCancels);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.RejectsEnabledKey),
                Arg.Any<bool>())
            .Returns(persistedEmailRejects);
        settings.GetAsync<bool>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.EnableSslKey),
                Arg.Any<bool>())
            .Returns(persistedEmailTls);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.HostKey),
                Arg.Any<string>())
            .Returns(persistedEmailHost);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.PortKey),
                Arg.Any<string>())
            .Returns(persistedEmailPort);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.FromKey),
                Arg.Any<string>())
            .Returns(persistedEmailFrom);
        settings.GetAsync<string>(
                Arg.Is<string>(section => section == EmailSink.SettingsSection),
                Arg.Is<string>(key => key == EmailSink.ToKey),
                Arg.Any<string>())
            .Returns(persistedEmailTo);
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

        return new MatrixEmailKit(viewModel, settings, changes, secrets);
    }

    #endregion Factories
}

/// <summary>One wired Notifications-section kit with every collaborator a test drives.</summary>
internal sealed record MatrixEmailKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    ISecretStore? Secrets);
