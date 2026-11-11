namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ZettaTrade.UI.Resources;

/// <summary>
/// The headless window world (T-144): one bare Application per process —
/// the same lazily-created session the chrome and publish probes already
/// share — grown until it hosts REAL windows. The growth is the app's own
/// surface, assembled by hand exactly like App.axaml does it: the Livery
/// token dictionary (the views' StaticResource lookups read their ramp and
/// spacing steps there), the Livery control styles (the chrome and the
/// panel scaffolds) and the REAL localization manager — the views'
/// <c>loc:Localized</c> markup resolves through the manager's singleton,
/// so a window materialization needs it initialized with the shipped
/// catalog, not a fake. Facts drain the serialized dispatcher world
/// through the pump, exactly like every dispatcher-riding suite.
/// </summary>
internal static class HeadlessWorld
{
    private const string TokensUri = "avares://Livery/Themes/Tokens.axaml";
    private const string ControlsUri = "avares://Livery/Themes/Controls.axaml";

    private static Task<Application>? _appTask;

    /// <summary>
    /// Ensure the world stands: the bare headless application with the
    /// app's resource merges, the control styles and the real localization
    /// singleton. Idempotent across classes — another test class may have
    /// stood the bare world up first; the missing pieces then add onto it.
    /// </summary>
    internal static Task<Application> EnsureAppAsync() => _appTask ??= SetupAsync();

    /// <summary>Show a window and land its open on the serialized dispatcher.</summary>
    internal static async Task ShowAndSettleAsync(Window window)
    {
        await EnsureAppAsync();
        Dispatcher.UIThread.Post(window.Show);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>
    /// Run one UI-thread function and land it through the serialized
    /// dispatcher: the windows materialize, resize and take their routed
    /// keys on the thread the pump owns — an off-thread constructor dies
    /// on VerifyAccess. A fault rethrows with its own stack and message.
    /// </summary>
    internal static async Task<T> OnUi<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        await AvaloniaDispatcherPump.DrainAsync();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    /// <summary>Run one UI-thread action, landing it through the dispatcher.</summary>
    internal static Task OnUi(Action action) =>
        OnUi<object?>(() =>
        {
            action();
            return null;
        });

    /// <summary>Close a window and land the close on the serialized dispatcher.</summary>
    internal static async Task CloseAndSettleAsync(Window window)
    {
        Dispatcher.UIThread.Post(window.Close);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>Every visual descendant of the given type, depth-first.</summary>
    internal static IEnumerable<T> Descendants<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// The first visual descendant of the given type carrying the given
    /// name — the facts' way to reach the windows' named controls
    /// (the code-behind's generated fields are private to the app).
    /// </summary>
    internal static T? FindDescendant<T>(Visual root, string name) where T : Control =>
        Descendants<Control>(root).OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>
    /// The theme singleton's health for one world (every world creation
    /// calls it): the widgets subscribe
    /// <c>ThemeManager.Instance.ThemeChanged</c> on attach, and a probe
    /// class's own manager poisons the pointer — its Dispose tears the
    /// subjects down but (unlike the localization manager's) never clears
    /// the static Instance, so the next window attach dies on
    /// CheckDisposed. Called inside the serialized singleton collection,
    /// so the check-then-heal cannot race a concurrent disposer.
    /// </summary>
    internal static async Task EnsureHealthyThemeSingletonAsync()
    {
        var needsHeal = await OnUi(() =>
        {
            if (ThemeManager.IsInitialized is false)
            {
                return true;
            }

            try
            {
                using var probe = ThemeManager.Instance.ThemeChanged.Subscribe(_ => { });
                return false;
            }
            catch (ObjectDisposedException)
            {
                // The pointer survived a probe's Dispose: heal.
                return true;
            }
        });

        if (needsHeal is false)
        {
            return;
        }

        // The bare constructor is the whole heal: it re-registers the
        // singleton and stands the live subjects up without touching any
        // disk (theme files load on InitializeAsync, which the facts never
        // need — their colors ride the merged resource dictionaries).
        await OnUi(() => _ = new ThemeManager(
            NullLogger.Instance,
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"headless-world-themes-{Guid.NewGuid():N}")));
    }

    private static async Task<Application> SetupAsync()
    {
        Application? app = null;
        Exception? failure = null;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                app = Application.Current;
                if (app is null)
                {
                    AppBuilder.Configure<Application>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                        .UseReactiveUI(rxui => { })
                        .SetupWithoutStarting();
                    app = Application.Current;
                }
                else
                {
                    // A pre-existing bare world (another probe class stood
                    // it up first) never met the ReactiveUI.Avalonia
                    // registration: the activation fetcher the
                    // ReactiveWindow sync asks for registers here instead —
                    // a duplicate over UseReactiveUI's own is harmless (the
                    // fetcher answers the same question twice).
                    Splat.Locator.CurrentMutable.Register(
                        () => new AvaloniaActivationForViewFetcher(),
                        typeof(IActivationForViewFetcher));
                }

                // The app's own presentation surface (App.axaml's shape):
                // the dark variant, Fluent, the Livery token dictionary the
                // views' StaticResource lookups read and the Livery control
                // styles. A double-merge guards nothing — the additions are
                // idempotent by their URI checks.
                app!.RequestedThemeVariant = ThemeVariant.Dark;
                if (app.Styles.OfType<FluentTheme>().Any() is false)
                {
                    app.Styles.Add(new FluentTheme());
                }

                if (app.Resources.MergedDictionaries.OfType<ResourceInclude>()
                        .Any(include => include.Source?.ToString() == TokensUri) is false)
                {
                    app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri(TokensUri))
                    {
                        Source = new Uri(TokensUri),
                    });
                }

                if (app.Styles.OfType<StyleInclude>()
                        .Any(include => include.Source?.ToString() == ControlsUri) is false)
                {
                    app.Styles.Add(new StyleInclude(new Uri(ControlsUri))
                    {
                        Source = new Uri(ControlsUri),
                    });
                }

                // The real localization singleton: the views' loc:Localized
                // markup resolves through it, so a window materialization
                // needs the shipped catalog behind it. First creation wins —
                // the manager's constructor installs the instance.
                if (LocalizationManager.IsInitialized is false)
                {
                    _ = new LocalizationManager(
                        NullLogger<LocalizationManager>.Instance,
                        Strings.ResourceManager);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        await AvaloniaDispatcherPump.DrainAsync();

        failure.Should().BeNull("the headless world must stand without failures");
        app.Should().NotBeNull("the world carries the application");
        return app!;
    }
}
