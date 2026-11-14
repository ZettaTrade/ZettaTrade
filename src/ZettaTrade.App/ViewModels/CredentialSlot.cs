namespace ZettaTrade.App.ViewModels;

using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>The status byte the commands exchange (the app standard alias).</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// One venue's credentials row in the settings window's exchange-credentials
/// section (T-227): the key and the secret draft into masked boxes, and the
/// Save commits BOTH to the secret store atomically — a half-typed pair
/// never reaches the store (the adapters refuse any missing half exactly
/// like a full absence, so the store holds only whole pairs). The store is
/// the pair's only home (the trading-safety canon: secrets never ride the
/// settings file, the logs, the command line or stdout); the boxes re-seed
/// empty on every window open (the secret never echoes), the status chip
/// answers from the store's presence, and Clear removes the pair.
/// </summary>
public sealed class CredentialSlot : ReactiveObject
{
    /// <summary>The secret keys the pair rides in the store (the settings' resx family, the adapter-owned spellings).</summary>
    private const string StatusSavedKey = "Settings.Credentials.StatusSaved";
    private const string StatusNotSetKey = "Settings.Credentials.StatusNotSet";
    private const string PairRequiredKey = "Settings.Credentials.PairRequired";

    private readonly ISecretStore? _secrets;
    private readonly ILocalizationManager _localization;
    private readonly ILogger _logger;

    private string _keyText = string.Empty;
    private string _secretText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSet;

    /// <summary>Create one venue's credentials row over the store's key names.</summary>
    /// <param name="venueTitle">The localized venue caption the row renders.</param>
    /// <param name="keySecretName">The ISecretStore key the API key commits to.</param>
    /// <param name="secretSecretName">The ISecretStore key the API secret commits to.</param>
    /// <param name="secrets">The secret store; null renders a memory-only row that commits nothing (the tests' world).</param>
    /// <param name="localization">The localized strings resolver.</param>
    /// <param name="logger">The settings surface's logger.</param>
    public CredentialSlot(
        string venueTitle,
        string keySecretName,
        string secretSecretName,
        ISecretStore? secrets,
        ILocalizationManager localization,
        ILogger logger)
    {
        VenueTitle = venueTitle;
        KeySecretName = keySecretName;
        SecretSecretName = secretSecretName;
        _secrets = secrets;
        _localization = localization;
        _logger = logger;

        var hasStore = secrets is not null;
        SaveCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => SaveAsync(cancellationToken),
            this.WhenAnyValue(
                x => x.KeyText,
                x => x.SecretText,
                (key, secret) => hasStore && key.Length > 0 && secret.Length > 0));
        ClearCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ClearAsync(cancellationToken),
            this.WhenAnyValue(x => x.IsSet));
    }

    /// <summary>The localized venue caption the row renders.</summary>
    public string VenueTitle { get; }

    /// <summary>The ISecretStore key the API key commits to (the adapter's own spelling).</summary>
    public string KeySecretName { get; }

    /// <summary>The ISecretStore key the API secret commits to (the adapter's own spelling).</summary>
    public string SecretSecretName { get; }

    /// <summary>The API key as typed (the box re-seeds empty; the value never echoes anything stored).</summary>
    public string KeyText
    {
        get => _keyText;
        set => this.RaiseAndSetIfChanged(ref _keyText, value);
    }

    /// <summary>The API secret as typed (masked; the same never-echoes rule).</summary>
    public string SecretText
    {
        get => _secretText;
        set => this.RaiseAndSetIfChanged(ref _secretText, value);
    }

    /// <summary>Whether the store holds a whole pair: the status chip's honest answer, read fresh on every refresh.</summary>
    public bool IsSet
    {
        get => _isSet;
        private set
        {
            if (this.RaiseAndSetIfChanged(ref _isSet, value))
            {
                this.RaisePropertyChanged(nameof(IsMissing));
            }
        }
    }

    /// <summary>True while the store holds no whole pair (the row's quiet prompt state).</summary>
    public bool IsMissing => !_isSet;

    /// <summary>The row's localized status line (the saved/not-set chip text; a refusal line rides here too).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>Commit both fields to the secret store atomically (the pair's only home).</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>Remove the stored pair (both keys), re-seed the boxes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }

    /// <summary>
    /// Read the pair's presence from the store (never its values) and render
    /// the honest status chip; a store-less row stays not-set. Runs on the
    /// window open and after every save/clear.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var store = _secrets;
        if (store is null)
        {
            IsSet = false;
            StatusText = _localization.GetString(StatusNotSetKey);
            return;
        }

        var key = await store.GetAsync(KeySecretName, ct);
        var secret = await store.GetAsync(SecretSecretName, ct);
        IsSet = !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret);
        StatusText = IsSet
            ? _localization.GetString(StatusSavedKey)
            : _localization.GetString(StatusNotSetKey);
    }

    private async Task<RxVoid> SaveAsync(CancellationToken ct)
    {
        var store = _secrets;
        if (store is null)
        {
            return RxVoid.Default;
        }

        var key = KeyText.Trim();
        var secret = SecretText;
        if (key.Length == 0 || secret.Length == 0)
        {
            // The canExecute gate holds this branch behind a filled pair, so
            // it only answers a race with the gate — the honest refusal
            // line, never a partial store write.
            StatusText = _localization.GetString(PairRequiredKey);
            return RxVoid.Default;
        }

        await store.SetAsync(KeySecretName, key, ct);
        await store.SetAsync(SecretSecretName, secret, ct);
        ReSeed();
        await RefreshAsync(ct);
        _logger.LogInformation(
            "The {Venue} credential pair saved to the secret store (keys '{Key}', '{Secret}')",
            VenueTitle,
            KeySecretName,
            SecretSecretName);
        return RxVoid.Default;
    }

    private async Task<RxVoid> ClearAsync(CancellationToken ct)
    {
        var store = _secrets;
        if (store is null)
        {
            return RxVoid.Default;
        }

        await store.RemoveAsync(KeySecretName, ct);
        await store.RemoveAsync(SecretSecretName, ct);
        ReSeed();
        await RefreshAsync(ct);
        _logger.LogInformation(
            "The {Venue} credential pair removed from the secret store",
            VenueTitle);
        return RxVoid.Default;
    }

    /// <summary>Clear both drafts (the secret never echoes, and a re-seed never writes the store).</summary>
    private void ReSeed()
    {
        KeyText = string.Empty;
        SecretText = string.Empty;
    }
}
