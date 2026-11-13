namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The tray close policy (T-214): what the main window's close gesture
/// does, kept live from the settings, plus the one exit flag every
/// explicit exit path arms before the shutdown lands. The main window's
/// closing interception consults this policy — an armed exit (the tray's
/// or the menu's Exit, the OS session end) always closes; the
/// MinimizeToTray mode turns a plain user close into a hide, so the
/// session keeps running behind the tray icon.
/// </summary>
/// <remarks>
/// The seed read settles INLINE in <see cref="InitializeAsync"/> (the
/// reactive settings canon): the first close gesture must see the stored
/// mode, never the default racing it. An unparsable stored value keeps
/// the default and logs — a UI preference never substitutes a guessed
/// mode, and nothing here is trading-relevant.
/// </remarks>
public sealed class TrayClosePolicy(
    ISettingsService settings,
    ILogger<TrayClosePolicy> logger) : IAsyncInitializable
{
    private readonly ISettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ILogger<TrayClosePolicy> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private volatile TrayCloseMode _mode = AppSettingsKeys.DefaultTrayCloseBehavior;

    private volatile bool _exitRequested;

    /// <summary>The standing close mode (the seed, kept live by the change stream).</summary>
    public TrayCloseMode Mode => _mode;

    /// <summary>Whether an explicit exit was requested (the flag every real close rides).</summary>
    public bool IsExitRequested => _exitRequested;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // The seed read settles INLINE: the first close gesture must see
        // the stored mode, never the default racing it.
        try
        {
            _mode = ParseMode(await _settings.GetAsync<string>(
                AppSettingsKeys.Section,
                AppSettingsKeys.TrayCloseBehavior,
                AppSettingsKeys.DefaultTrayCloseBehavior.ToString())
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the tray close behavior");
        }

        // The change stream keeps the mode current without a restart: the
        // tray menu's checkable (or an external edit) re-applies live. An
        // unparsable payload never poisons the standing mode — it logs and
        // the standing truth holds.
        _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.TrayCloseBehavior)
            .Select(change => change.NewValue as string)
            .Subscribe(
                value =>
                {
                    if (TryParseMode(value, out var parsed))
                    {
                        _mode = parsed;
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _logger.LogWarning(
                            "Unknown live tray close behavior '{Mode}'; keeping {Standing}",
                            value,
                            _mode);
                    }
                },
                ex => _logger.LogError(ex, "The tray close behavior stream stopped unexpectedly"));
    }

    /// <summary>
    /// Arm the exit: the next close gesture closes regardless of the mode.
    /// The tray's and the menu's Exit calls, and the OS session end (the
    /// lifetime's ShutdownRequested), all land here — an explicit exit is
    /// never swallowed by the close-to-tray interception.
    /// </summary>
    public void ArmExit() => _exitRequested = true;

    /// <summary>
    /// The explicit exit path: arm the flag, then shut the application
    /// down (the same lifetime route the File menu's Exit rides). A
    /// missing desktop lifetime (the headless world) skips the shutdown
    /// silently — the flag is the part the close interception reads.
    /// </summary>
    public void RequestExit()
    {
        ArmExit();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// Whether the pending close may proceed: an armed exit always does;
    /// otherwise only the shipped Exit mode lets the close through.
    /// </summary>
    public bool ShouldAllowClose() => _exitRequested || _mode != TrayCloseMode.MinimizeToTray;

    /// <summary>
    /// Parse a stored mode name for the seed read: an unparsable value
    /// keeps the default (the shipped semantics) and logs — never a guess.
    /// </summary>
    private TrayCloseMode ParseMode(string? stored)
    {
        if (TryParseMode(stored, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(stored))
        {
            _logger.LogWarning(
                "Unknown stored tray close behavior '{Mode}'; keeping {Default}",
                stored,
                AppSettingsKeys.DefaultTrayCloseBehavior);
        }

        return AppSettingsKeys.DefaultTrayCloseBehavior;
    }

    /// <summary>
    /// Try to parse a mode name: an unparsable value parses to nothing and
    /// the caller keeps its standing truth (the default on the seed read,
    /// the standing mode on the live stream).
    /// </summary>
    private static bool TryParseMode(string? stored, out TrayCloseMode parsed)
    {
        if (stored is not null && Enum.TryParse(stored, ignoreCase: true, out parsed))
        {
            return true;
        }

        parsed = AppSettingsKeys.DefaultTrayCloseBehavior;
        return false;
    }
}
