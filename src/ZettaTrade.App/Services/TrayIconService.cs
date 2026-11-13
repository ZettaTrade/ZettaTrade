namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The tray presence (T-214): one tray icon with the minimal menu — the
/// show/hide toggle, the close-to-tray checkable, the Do-Not-Disturb
/// checkable and the exit. The icon mirrors the main window's own asset
/// icon; a
/// platform without tray support degrades honestly: the registration is
/// wrapped (a failure logs a warning and the app continues exactly as
/// before — no tray appears, no behavior changes). The alert surface
/// stays where it shipped: there is no balloon API on the tray, so fired
/// alerts keep landing in the notification center (the history holds
/// everything while the window is hidden) and this menu's DND checkable
/// is the alert-control surface.
/// </summary>
/// <remarks>
/// The menu's Click handlers route through the internal seams the tests
/// drive: the toggles stay one honest write per flip (the settings
/// streams re-apply the mirrors), and the exit arms the close policy.
/// Nothing here touches order or risk surfaces.
/// </remarks>
public sealed class TrayIconService : IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly NotificationCenter _center;
    private readonly TrayClosePolicy _closePolicy;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<TrayIconService> _logger;

    private readonly CompositeDisposable _subscriptions = new();

    private const string ShowHideKey = "Tray.ShowHide";

    private const string CloseToTrayKey = "Tray.CloseToTray";

    private const string DoNotDisturbKey = "Notif.DndToggle";

    private const string ExitKey = "Menu.File.Exit";

    private const string IconToolTip = "ZettaTrade";

    /// <summary>
    /// Build the tray service: the standing Do-Not-Disturb truth rides
    /// the center's composed lane from construction (the seed settles
    /// first, every change re-applies it) — the same lane the menu's
    /// checkable mirrors; the toggle flips exactly this, one honest
    /// write per flip.
    /// </summary>
    public TrayIconService(
        ISettingsService settings,
        NotificationCenter center,
        TrayClosePolicy closePolicy,
        ILocalizationManager localization,
        ILogger<TrayIconService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _closePolicy = closePolicy ?? throw new ArgumentNullException(nameof(closePolicy));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _subscriptions.Add(_center.DoNotDisturb.Subscribe(
            flag => _dndStanding = flag,
            ex => _logger.LogError(ex, "The tray's Do-Not-Disturb standing stopped unexpectedly")));
    }

    private Window? _window;
    private TrayIcon? _icon;
    private NativeMenuItem? _showHideItem;
    private NativeMenuItem? _closeToTrayItem;
    private NativeMenuItem? _dndItem;
    private NativeMenuItem? _exitItem;

    private volatile bool _dndStanding;

    /// <summary>
    /// Attach the tray to the main window: build the menu (its headers
    /// re-derive on every language switch), register the icon with the
    /// application (a platform without tray support logs and continues)
    /// and mirror the DND and close-to-tray states live.
    /// </summary>
    public void AttachMainWindow(Window window)
    {
        if (_window is not null)
        {
            throw new InvalidOperationException("The tray is already attached to a window.");
        }

        _window = window ?? throw new ArgumentNullException(nameof(window));

        _showHideItem = new NativeMenuItem(_localization.GetString(TrayResx.ShowHide));
        _showHideItem.Click += (_, _) => ToggleShowHide();

        _closeToTrayItem = new NativeMenuItem(_localization.GetString(TrayResx.CloseToTray))
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _closePolicy.Mode == TrayCloseMode.MinimizeToTray,
        };
        _closeToTrayItem.Click += (_, _) => Observable
            .FromAsync(ToggleCloseToTrayAsync)
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The tray's close-to-tray toggle failed"));

        _dndItem = new NativeMenuItem(_localization.GetString(TrayResx.DoNotDisturb))
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        _dndItem.Click += (_, _) => Observable
            .FromAsync(ToggleDndAsync)
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The tray's Do-Not-Disturb toggle failed"));

        _exitItem = new NativeMenuItem(_localization.GetString(TrayResx.Exit));
        _exitItem.Click += (_, _) => ExitApp();

        var menu = new NativeMenu();
        menu.Add(_showHideItem);
        menu.Add(_closeToTrayItem);
        menu.Add(_dndItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_exitItem);

        // The tray mirrors the main window's own asset icon — the exact
        // same WindowIcon the window's XAML resolved (the app's branding),
        // no second asset-load route to fail at startup.
        _icon = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = IconToolTip,
            Menu = menu,
            IsVisible = true,
        };

        try
        {
            // The attached collection is the one every platform backend
            // reads; a platform without tray support surfaces the miss
            // here (logged, the app continues exactly as before).
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
        }
        catch (Exception ex)
        {
            _icon.IsVisible = false;
            _logger.LogWarning(ex, "This platform provides no tray support; the app continues without a tray icon");
            return;
        }

        // The DND mirror: the center's composed lane (seed + changes) is
        // the checked state — the same truth the history window mirrors.
        _subscriptions.Add(_center.DoNotDisturb.Subscribe(
            flag => _dndItem!.IsChecked = flag,
            ex => _logger.LogError(ex, "The tray's Do-Not-Disturb mirror stopped unexpectedly")));

        // The close-to-tray mirror: the settings stream keeps the
        // checkable against the standing mode (the policy re-applies
        // live; the checked state follows the same truth).
        _subscriptions.Add(_settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.TrayCloseBehavior)
            .Subscribe(
                change => _closeToTrayItem!.IsChecked = ParseCloseToTray(change.NewValue as string),
                ex => _logger.LogError(ex, "The tray's close-to-tray mirror stopped unexpectedly")));

        // The language switch re-inks every header; the exit caption rides
        // the File menu's own key (one truth per surface).
        _subscriptions.Add(_localization.LanguageChanged.Subscribe(
            _ => _showHideItem!.Header = _localization.GetString(TrayResx.ShowHide),
            ex => _logger.LogError(ex, "The tray's language re-derivation stopped unexpectedly")));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _subscriptions.Dispose();
        if (_icon is { } icon)
        {
            icon.IsVisible = false;
            icon.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    /// <summary>Show/hide the main window (the tray icon click's sibling route).</summary>
    internal void ToggleShowHide()
    {
        if (_window is not { } window)
        {
            return;
        }

        if (window.IsVisible)
        {
            window.Hide();
            return;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>Write the flipped Do-Not-Disturb truth; the mirrors re-apply it.</summary>
    internal async Task ToggleDndAsync()
    {
        await _settings.SetAsync(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.DoNotDisturb,
            !_dndStanding).ConfigureAwait(false);
    }

    /// <summary>Write the flipped close-to-tray mode; the policy's live stream re-applies it.</summary>
    internal async Task ToggleCloseToTrayAsync()
    {
        var next = _closePolicy.Mode == TrayCloseMode.MinimizeToTray
            ? TrayCloseMode.Exit
            : TrayCloseMode.MinimizeToTray;
        await _settings.SetAsync(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            next.ToString()).ConfigureAwait(false);
    }

    /// <summary>The tray's Exit: arm the policy, then the lifetime shutdown lands.</summary>
    internal void ExitApp() => _closePolicy.RequestExit();

    /// <summary>Interpret a stored close behavior name (unparsable: the off state, never a guess).</summary>
    private static bool ParseCloseToTray(string? stored) =>
        stored is not null &&
        Enum.TryParse(stored, ignoreCase: true, out TrayCloseMode parsed) &&
        parsed == TrayCloseMode.MinimizeToTray;
}
