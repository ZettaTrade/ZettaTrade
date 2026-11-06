namespace ZettaTrade.App.Services;

using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// UI-based confirmation service.
/// Shows the MVVM confirmation dialog (<see cref="Views.ConfirmationDialogWindow"/>,
/// <see cref="ViewModels.ConfirmationDialogViewModel"/>) and waits for the user's answer.
/// </summary>
public sealed class UiConfirmationService : IConfirmationService
{
    private readonly ILogger<UiConfirmationService> _logger;
    private readonly ILocalizationManager _localization;

    /// <summary>Confirmation dialog timeout in seconds.</summary>
    private const int ConfirmationTimeoutSeconds = 60;

    /// <summary>
    /// Create a new UiConfirmationService instance.
    /// </summary>
    public UiConfirmationService(ILogger<UiConfirmationService> logger, ILocalizationManager localization)
    {
        _logger = logger;
        _localization = localization;
    }

    /// <inheritdoc/>
    public Task<bool> RequestConfirmationAsync(string message, CancellationToken ct = default) =>
        ShowDialogAsync(message, ct);

    /// <inheritdoc/>
    public async Task<bool> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The template is resolved in the current language here; AI layers never
        // compose user-facing text themselves.
        var message = _localization.GetString(request.MessageKey, request.Arguments);
        return await ShowDialogAsync(message, ct);
    }

    private async Task<bool> ShowDialogAsync(string message, CancellationToken ct)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
            {
                _logger.LogError("Cannot show confirmation dialog: no desktop lifetime");
                return false;
            }

            var mainWindow = lifetime.MainWindow;
            if (mainWindow is null)
            {
                _logger.LogError("Cannot show confirmation dialog: no main window");
                return false;
            }

            var dialog = new ConfirmationDialogWindow
            {
                DataContext = new ConfirmationDialogViewModel(message),
            };

            // Shared timeout budget: closes the window so ShowDialog cannot hang forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ConfirmationTimeoutSeconds));
            using var registration = timeoutCts.Token.Register(
                () => Dispatcher.UIThread.Post(dialog.Close));

            await dialog.ShowDialog<object>(mainWindow);

            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                _logger.LogWarning("Confirmation dialog timed out");

            // No explicit answer (title bar X, timeout) is never a confirmation.
            return dialog.Result ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show confirmation dialog");
            return false;
        }
    }
}

