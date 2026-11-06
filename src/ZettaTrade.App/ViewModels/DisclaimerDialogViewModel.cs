namespace ZettaTrade.App.ViewModels;

using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel of the first-run disclaimer dialog. The dialog body is static
/// localized text ({loc:Localized} in the XAML), so the ViewModel owns the
/// acknowledge command only: executing it persists the shipped disclaimer
/// version through the settings store (idempotent — the Help-menu re-show
/// acknowledges an already-current version the same way).
/// </summary>
public sealed class DisclaimerDialogViewModel : ReactiveObject
{
    private readonly ISettingsService _settings;
    private readonly ILogger<DisclaimerDialogViewModel> _logger;

    /// <summary>
    /// Create a new DisclaimerDialogViewModel instance.
    /// </summary>
    /// <param name="settings">The settings store the acknowledgment persists through.</param>
    /// <param name="logger">The logger (file-only — Serilog never writes to stdout).</param>
    public DisclaimerDialogViewModel(
        ISettingsService settings,
        ILogger<DisclaimerDialogViewModel> logger)
    {
        _settings = settings;
        _logger = logger;
        AcknowledgeCommand = ReactiveCommand.CreateFromTask(AcknowledgeAsync);
    }

    /// <summary>Persist the shipped disclaimer version as acknowledged.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AcknowledgeCommand { get; }

    /// <summary>
    /// The acknowledge write. A failed write is logged HERE (not at the
    /// stream) and the command still finishes normally: the dialog closes,
    /// the unset flag re-shows the disclaimer on the next launch.
    /// </summary>
    private async Task AcknowledgeAsync()
    {
        try
        {
            await DisclaimerAcknowledgment.AcknowledgeAsync(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the disclaimer acknowledgment");
        }
    }
}
