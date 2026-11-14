namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>The status byte the commands exchange (the app standard alias).</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The settings' per-venue credentials row (T-227): the Save commits BOTH
/// keys to the secret store atomically (a half-typed pair never reaches the
/// store), the boxes re-seed empty after every commit (the secret never
/// echoes), the status chip answers from the store's presence only, Clear
/// removes the pair, and a store-less row (the tests' world) commits
/// nothing and stays honest.
/// </summary>
public sealed class CredentialSlotTests
{
    #region Kit

    /// <summary>A deterministic resolver: the fake answers the key itself, so the chip asserts name the exact string.</summary>
    private sealed class FixedLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) =>
            Task.CompletedTask;

        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) => key;

        public IObservable<string> LanguageChanged => System.Reactive.Linq.Observable.Empty<string>();

        public IObservable<string> GetObservable(string key) => System.Reactive.Linq.Observable.Empty<string>();
    }

    /// <summary>An in-memory secret store: the round-trip's honest stand-in with per-key contents.</summary>
    private sealed class InMemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Pairs { get; } = new();

        public int SetCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Pairs.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            SetCalls++;
            Pairs[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            RemoveCalls++;
            Pairs.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. Pairs.Keys]);

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static CredentialSlot BuildSlot(ISecretStore? secrets)
    {
        var slot = new CredentialSlot(
            venueTitle: "Alpaca",
            keySecretName: "alpaca.api-key",
            secretSecretName: "alpaca.api-secret",
            secrets: secrets,
            localization: new FixedLocalization(),
            logger: NullLogger<CredentialSlot>.Instance);
        return slot;
    }

    private static async Task ExecuteAsync(ReactiveCommand<RxVoid, RxVoid> command) =>
        await command.Execute(RxVoid.Default);

    /// <summary>Read the command's current gate: the observable answers on subscribe.</summary>
    private static bool CanExecuteNow(ReactiveCommand<RxVoid, RxVoid> command)
    {
        var gate = false;
        using var watch = command.CanExecute.Subscribe(guard => gate = guard);
        return gate;
    }

    #endregion Kit

    #region The save round-trip

    [Fact]
    public async Task TheSave_CommitsBothKeys_ReseedsTheBoxesAndAnswersSaved()
    {
        var store = new InMemorySecretStore();
        var slot = BuildSlot(store);

        await slot.RefreshAsync();
        slot.IsSet.Should().BeFalse("an empty store answers not-set");
        slot.StatusText.Should().Be("Settings.Credentials.StatusNotSet");

        slot.KeyText = "cred-key";
        slot.SecretText = "cred-secret";
        await ExecuteAsync(slot.SaveCommand);

        store.Pairs.Should().Contain("alpaca.api-key", "cred-key");
        store.Pairs.Should().Contain("alpaca.api-secret", "cred-secret");
        store.SetCalls.Should().Be(2, "the whole pair commits, both keys");
        slot.KeyText.Should().BeEmpty("the boxes re-seed empty; the secret never echoes");
        slot.SecretText.Should().BeEmpty();
        slot.IsSet.Should().BeTrue();
        slot.StatusText.Should().Be("Settings.Credentials.StatusSaved");
    }

    [Fact]
    public async Task TheHalfTypedSave_NeverReachesTheStore()
    {
        var store = new InMemorySecretStore();
        var slot = BuildSlot(store);

        slot.KeyText = "cred-key";
        // The secret half is still empty: the typed refusal, no store write.
        await ExecuteAsync(slot.SaveCommand);

        store.SetCalls.Should().Be(0, "a half-typed pair never persists");
        store.Pairs.Should().BeEmpty();
        slot.StatusText.Should().Be("Settings.Credentials.PairRequired");
    }

    [Fact]
    public async Task TheClear_RemovesBothKeysAndReSeedsTheBoxes()
    {
        var store = new InMemorySecretStore();
        var slot = BuildSlot(store);
        slot.KeyText = "cred-key";
        slot.SecretText = "cred-secret";
        await ExecuteAsync(slot.SaveCommand);

        await slot.RefreshAsync();
        await ExecuteAsync(slot.ClearCommand);

        store.Pairs.Should().BeEmpty("clear removes the whole pair, both keys");
        store.RemoveCalls.Should().Be(2);
        slot.KeyText.Should().BeEmpty();
        slot.IsSet.Should().BeFalse();
        slot.StatusText.Should().Be("Settings.Credentials.StatusNotSet");
    }

    #endregion The save round-trip

    #region The gates

    [Fact]
    public void TheSaveGate_HoldsUntilTheWholePairIsTyped()
    {
        var slot = BuildSlot(new InMemorySecretStore());

        CanExecuteNow(slot.SaveCommand).Should().BeFalse("an empty draft saves nothing");
        slot.KeyText = "cred-key";
        CanExecuteNow(slot.SaveCommand).Should().BeFalse("the key alone is half a pair");
        slot.SecretText = "cred-secret";
        CanExecuteNow(slot.SaveCommand).Should().BeTrue("the whole pair may commit");
    }

    [Fact]
    public async Task TheStoreLessRow_CommitsNothingAndStaysHonest()
    {
        var slot = BuildSlot(secrets: null);

        await slot.RefreshAsync();
        slot.IsSet.Should().BeFalse();
        slot.StatusText.Should().Be("Settings.Credentials.StatusNotSet");
        CanExecuteNow(slot.SaveCommand).Should().BeFalse(
            "a store-less row saves nothing, memory-only");
        CanExecuteNow(slot.ClearCommand).Should().BeFalse(
            "nothing set means nothing to clear");

        await ExecuteAsync(slot.SaveCommand);
        await ExecuteAsync(slot.ClearCommand);
        slot.IsSet.Should().BeFalse("the store-less row stays honest through both commands");
    }

    #endregion The gates
}
