namespace ZettaTrade.App.Services;

using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Resources;

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

    private static string Localize(string key)
    {
        return Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
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
                Title = Localize("Trading.ConfirmationTitle"),
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
                Content = Localize("Trading.Confirm"),
                Width = 100,
            };

            var cancelButton = new Button
            {
                Content = Localize("Trading.Cancel"),
                Width = 100,
            };

            var tcs = new TaskCompletionSource<bool>();

            confirmButton.Click += (_, _) =>
            {
                tcs.TrySetResult(true);
                dialog.Close();
            };

            cancelButton.Click += (_, _) =>
            {
                tcs.TrySetResult(false);
                dialog.Close();
            };

            buttonPanel.Children.Add(confirmButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(messageText);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;

            // Shared timeout budget: covers dialog open time and the answer wait.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ConfirmationTimeoutSeconds));

            // Close the dialog on timeout so ShowDialog cannot hang forever.
            using var registration = timeoutCts.Token.Register(
                () => Dispatcher.UIThread.Post(dialog.Close));

            await dialog.ShowDialog<object>(mainWindow);

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
