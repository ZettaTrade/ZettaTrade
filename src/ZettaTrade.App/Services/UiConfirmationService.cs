namespace ZettaTrade.App.Services;

using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// UI-based confirmation service.
/// Shows a dialog to the user and waits for confirmation.
/// </summary>
public sealed class UiConfirmationService : IConfirmationService
{
    private readonly ILogger<UiConfirmationService> _logger;
    
    /// <summary>Confirmation dialog timeout in seconds.</summary>
    private const int ConfirmationTimeoutSeconds = 60;

    /// <summary>
    /// Create a new UiConfirmationService instance.
    /// </summary>
    public UiConfirmationService(ILogger<UiConfirmationService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> RequestConfirmationAsync(string message, CancellationToken ct = default)
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

            var dialog = new Window
            {
                Title = "Confirmation Required",
                Width = 400,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var panel = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
            };

            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 10,
            };

            var confirmButton = new Button
            {
                Content = "Confirm",
                Width = 100,
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 100,
            };

            var tcs = new TaskCompletionSource<bool>();

            confirmButton.Click += (_, _) =>
            {
                dialog.Close();
                tcs.TrySetResult(true);
            };

            cancelButton.Click += (_, _) =>
            {
                dialog.Close();
                tcs.TrySetResult(false);
            };

            buttonPanel.Children.Add(confirmButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(messageText);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ConfirmationTimeoutSeconds));

            await dialog.ShowDialog<object>(mainWindow);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConfirmationTimeoutSeconds));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Confirmation dialog timed out");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show confirmation dialog");
            return false;
        }
    }
}
