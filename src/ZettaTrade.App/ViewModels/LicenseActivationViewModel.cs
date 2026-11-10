namespace ZettaTrade.App.ViewModels;

using System.Reactive.Disposables;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// ViewModel of the license activation dialog (T-132): the operator pastes
/// the plugin id and the issued key, the activate command runs the local
/// validation through <see cref="ILicenseActivationService"/> and the
/// verdict re-renders in the current language (a runtime language switch
/// re-renders a still-shown verdict too). A module carrying EULA text
/// surfaces it at the first activation and stores nothing until the
/// accept box is ticked. The dialog body's static text is localized in
/// XAML; the verdict is dynamic content mapped through the localization
/// manager — the key mapping lives here, never a localized string property.
/// </summary>
public sealed class LicenseActivationViewModel : ReactiveObject
{
    private readonly ILicenseActivationService _activation;
    private readonly ILicenseEulaService _eula;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<LicenseActivationViewModel> _logger;
    private string? _verdictKey;

    private string _pluginId = string.Empty;

    private string _licenseKey = string.Empty;

    private string _verdict = string.Empty;

    private bool _stored;

    private bool _hasEula;

    private string _eulaText = string.Empty;

    private bool _eulaAccepted;

    /// <summary>
    /// Create a new LicenseActivationViewModel instance.
    /// </summary>
    /// <param name="activation">The local activation service (validate, store only on valid).</param>
    /// <param name="eula">The module's EULA surface (first-activation consent leg).</param>
    /// <param name="localization">The localization manager the verdict renders through.</param>
    /// <param name="logger">The logger (file-only — Serilog never writes to stdout).</param>
    public LicenseActivationViewModel(
        ILicenseActivationService activation,
        ILicenseEulaService eula,
        ILocalizationManager localization,
        ILogger<LicenseActivationViewModel> logger)
    {
        _activation = activation;
        _eula = eula;
        _localization = localization;
        _logger = logger;

        var canActivate = this.WhenAnyValue(
            x => x.PluginId,
            x => x.LicenseKey,
            x => x.HasEula,
            x => x.Accepted,
            (pluginId, licenseKey, hasEula, accepted) =>
                !string.IsNullOrWhiteSpace(pluginId)
                && !string.IsNullOrWhiteSpace(licenseKey)
                && (!hasEula || accepted));
        ActivateCommand = ReactiveCommand.CreateFromTask(ActivateAsync, canActivate);
    }

    /// <summary>The plugin id the pasted key must bind to (trimmed on activation).</summary>
    public string PluginId
    {
        get => _pluginId;
        set => this.RaiseAndSetIfChanged(ref _pluginId, value);
    }

    /// <summary>The pasted license key text (trimmed on activation).</summary>
    public string LicenseKey
    {
        get => _licenseKey;
        set => this.RaiseAndSetIfChanged(ref _licenseKey, value);
    }

    /// <summary>
    /// The activation verdict, rendered through the localization manager in
    /// the current language; empty before the first activation.
    /// </summary>
    public string Verdict
    {
        get => _verdict;
        private set => this.RaiseAndSetIfChanged(ref _verdict, value);
    }

    /// <summary>
    /// Whether the last activation stored the key (a valid verdict). The
    /// window closes only on a stored key — a refusal leaves the dialog
    /// open with the verdict shown.
    /// </summary>
    public bool Stored
    {
        get => _stored;
        private set => this.RaiseAndSetIfChanged(ref _stored, value);
    }

    /// <summary>Whether the module carried EULA text that is now shown for acceptance (a first-activation fact per plugin id).</summary>
    public bool HasEula
    {
        get => _hasEula;
        private set => this.RaiseAndSetIfChanged(ref _hasEula, value);
    }

    /// <summary>The module's EULA text as read from its package (empty until the first-activation surface).</summary>
    public string EulaText
    {
        get => _eulaText;
        private set => this.RaiseAndSetIfChanged(ref _eulaText, value);
    }

    /// <summary>
    /// Whether the operator ticked the accept box for the shown EULA (the
    /// store waits for it). Bind target — the checkbox writes it.
    /// </summary>
    public bool Accepted
    {
        get => _eulaAccepted;
        set => this.RaiseAndSetIfChanged(ref _eulaAccepted, value);
    }

    /// <summary>The activate command (enabled while both inputs carry text and a shown EULA carries the accept tick).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ActivateCommand { get; }

    /// <summary>
    /// Attach the verdict rendering the window owns: a runtime language
    /// switch re-renders a still-shown verdict, and a plugin-id change
    /// resets the EULA leg (a shown agreement belongs to one module, never
    /// to the next paste). The caller disposes the returned subscription
    /// with the window (<c>WhenActivated</c> + <c>DisposeWith</c> — the
    /// canon the transient view model and its only window share).
    /// </summary>
    public IDisposable AttachVerdictRendering() => new CompositeDisposable(
        _localization.LanguageChanged.Subscribe(
            _ => RenderVerdict(),
            ex => _logger.LogError(ex, "The license activation verdict's language stream failed")),
        this.WhenAnyValue(x => x.PluginId).Subscribe(
            _ => ResetEula(),
            ex => _logger.LogError(ex, "The license activation EULA reset stream failed")));

    private async Task ActivateAsync()
    {
        _verdictKey = null;
        try
        {
            var pluginId = PluginId.Trim();

            // The first activation of a module carrying EULA text surfaces
            // the agreement and requires the tick before anything is
            // validated or stored (a persisted acceptance keeps the later
            // activations straight to validation). The mark persists right
            // before the first validated attempt — reading the agreement is
            // the customer's fact, independent of the key's verdict.
            var eulaText = await _eula.GetTextAsync(pluginId);
            if (eulaText is not null)
            {
                var persisted = await _eula.IsAcceptedAsync(pluginId);
                if (persisted)
                {
                    Accepted = true;
                }

                if (!persisted && !Accepted)
                {
                    HasEula = true;
                    EulaText = eulaText;
                    _verdictKey = EulaRequiredVerdictKey;
                    RenderVerdict();
                    return;
                }

                if (!persisted)
                {
                    await _eula.MarkAcceptedAsync(pluginId);
                }
            }

            var status = await _activation.ActivateAsync(
                pluginId,
                LicenseKey.Trim());
            _verdictKey = VerdictKey(status);
            Stored = status == LicenseValidationStatus.Valid;
        }
        catch (Exception ex)
        {
            // The activation leg must never take the dialog down: the
            // verdict degrades to the explicit failure text, the refusal
            // (nothing stored) stands, the detail lands in the file log.
            _verdictKey = FailedVerdictKey;
            Stored = false;
            _logger.LogError(ex, "The license activation failed unexpectedly; nothing stored");
        }

        RenderVerdict();
    }

    private void ResetEula()
    {
        HasEula = false;
        EulaText = string.Empty;
        Accepted = false;
    }

    private void RenderVerdict() =>
        Verdict = _verdictKey is not { } key ? string.Empty : _localization.GetString(key);

    private static string VerdictKey(LicenseValidationStatus status) => status switch
    {
        LicenseValidationStatus.Valid => "Settings.License.Verdict.Valid",
        LicenseValidationStatus.Missing => "Settings.License.Verdict.Missing",
        LicenseValidationStatus.Malformed => "Settings.License.Verdict.Malformed",
        LicenseValidationStatus.Tampered => "Settings.License.Verdict.Tampered",
        LicenseValidationStatus.Expired => "Settings.License.Verdict.Expired",
        LicenseValidationStatus.WrongProduct => "Settings.License.Verdict.WrongProduct",
        _ => "Settings.License.Verdict.Unconfigured",
    };

    private const string EulaRequiredVerdictKey = "Settings.License.Verdict.EulaRequired";

    private const string FailedVerdictKey = "Settings.License.Verdict.Failed";
}
