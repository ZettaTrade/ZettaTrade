namespace ZettaTrade.App.Tests.Views;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Tests;

/// <summary>
/// The derived system-key publish facts (T-150): ThemeManager's apply must
/// serve the stock control templates' own families from the applied
/// palette, at every lookup layer — the application's own resource
/// dictionary (nothing shadows the publish there), the application
/// resource walk (where the hosted Fluent dictionary also answers) and a
/// live window's control chain — and a second apply must retint the same
/// world without a restart (the hot-reload mechanic's apply path). The
/// stock family resolves pre-apply too: the before-value resolves Fluent's
/// built-in family, so the after-asserts cannot fake themselves.
/// The world is dispatcher-affine (the resources live on the pump thread):
/// theme files register off-thread (the manager's registry is its own
/// thread-safe world), the apply and every assertion run inside posted
/// drain jobs — a never-drained job never executes, so no fact leaks state
/// into another's (the pump's own protocol). The class rides the
/// LocalizationManagerSingleton collection: its own ThemeManager
/// construction swaps and its Dispose poisons the static Instance for
/// every later window attach, so the manager-creating and
/// manager-consuming classes run strictly sequentially.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class DerivedSystemKeysPublishTests
{
    private static Task<(Application App, Window Probe)>? _appWindowTask;

    static DerivedSystemKeysPublishTests()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
    }

    /// <summary>The pin palette (A): the dark counter baseline.</summary>
    private static readonly IReadOnlyDictionary<string, string> PaletteA = new Dictionary<string, string>
    {
        ["Background"] = "#101010",
        ["Surface"] = "#1a1a28",
        ["Border"] = "#202028",
        ["Accent"] = "#123456",
        ["TextPrimary"] = "#eeeeee",
    };

    /// <summary>The pin palette (B): light and completely recast — a second
    /// apply must retint the derived family without a restart.</summary>
    private static readonly IReadOnlyDictionary<string, string> PaletteB = new Dictionary<string, string>
    {
        ["Background"] = "#fafafa",
        ["Surface"] = "#f0f0f0",
        ["Border"] = "#e0e0e0",
        ["Accent"] = "#aa00aa",
        ["TextPrimary"] = "#111111",
    };

    [Fact]
    public async Task ApplyingTheTheme_PublishesTheDerivedFamilyIntoTheResourceWorld()
    {
        var manager = await RegisterTheme("derived-a", PaletteA);
        var (app, probe) = await EnsureAppWindow();

        await Run(() =>
        {
            // Before the apply: the stock family answers the chain (Fluent's
            // built-in brush), so the after-asserts cannot fake themselves.
            probe.TryFindResource(
                "SystemControlForegroundBaseHighBrush", ThemeVariant.Dark, out _)
                .Should().BeTrue("Fluent serves its own system family before any apply");

            // The apply is the publish: run it on the resource world's own
            // thread and read every layer in the same job.
            manager.ApplyThemeAsync("derived-a").GetAwaiter().GetResult();

            // The application's own dictionary: the publish, exactly. The
            // brush-family members publish as brushes — Avalonia 12's
            // property store enforces value-type identity per effectively
            // bound entry (a stock template resolved the key with a brush
            // first, so a raw Color would throw the store wide; 0.1.3's
            // fix).
            app.Resources["SystemAccentColor"].Should().Be(Color.Parse("#123456"));
            ((SolidColorBrush)app.Resources["SystemControlForegroundBaseMediumLowBrush"]!)
                .Color.Should().Be(Color.Parse("#5e5e5e"));
            ((SolidColorBrush)app.Resources["SystemControlDisabledBaseHighBrush"]!)
                .Color.Should().Be(Color.Parse("#747474"));
            ((SolidColorBrush)app.Resources["SystemControlBackgroundAltHighBrush"]!)
                .Color.Should().Be(Color.Parse("#141418"));
            ((SolidColorBrush)app.Resources["SystemControlHighlightAccentBrush"]!)
                .Color.Should().Be(Color.Parse("#123456"));
            app.Resources.Should().NotContainKey("SystemControlForegroundChromeGrayBrush",
                "the publish is the bounded derived family, not a Fluent-dictionary replay");

            // The application walk and the window's control chain answer the
            // SAME derived values: a hosted Fluent dictionary never shadows
            // the palette-derived publish.
            app.TryGetResource(
                "SystemControlForegroundBaseMediumLowBrush", ThemeVariant.Dark, out var fromApp)
                .Should().BeTrue();
            fromApp.Should().BeOfType<SolidColorBrush>("the derived brush keys publish as brushes");
            ((SolidColorBrush)fromApp!).Color.Should().Be(Color.Parse("#5e5e5e"));

            probe.TryFindResource(
                "SystemAccentColor", ThemeVariant.Dark, out var fromChain)
                .Should().BeTrue();
            fromChain.Should().Be(Color.Parse("#123456"));
        });
    }

    [Fact]
    public async Task TheSecondApply_RetintsTheSameWorldLive()
    {
        var managerA = await RegisterTheme("derived-c-1", PaletteA);
        var managerB = await RegisterTheme("derived-c-2", PaletteB);
        var (app, probe) = await EnsureAppWindow();

        await Run(() =>
        {
            managerA.ApplyThemeAsync("derived-c-1").GetAwaiter().GetResult();
            app.Resources["SystemAccentColor"].Should().Be(Color.Parse("#123456"));
            ((SolidColorBrush)app.Resources["SystemControlForegroundBaseMediumLowBrush"]!)
                .Color.Should().Be(Color.Parse("#5e5e5e"));
        });

        await Run(() =>
        {
            managerB.ApplyThemeAsync("derived-c-2").GetAwaiter().GetResult();

            app.Resources["SystemAccentColor"].Should().Be(Color.Parse("#aa00aa"));
            ((SolidColorBrush)app.Resources["SystemControlForegroundBaseMediumLowBrush"]!)
                .Color.Should().Be(Color.Parse("#a8a8a8"));
            ((SolidColorBrush)app.Resources["SystemControlBackgroundAltHighBrush"]!)
                .Color.Should().Be(Color.Parse("#f7f7f7"));
            ((SolidColorBrush)app.Resources["SystemControlDisabledBaseHighBrush"]!)
                .Color.Should().Be(Color.Parse("#919191"));
            ((SolidColorBrush)app.Resources["SystemControlHighlightAccentBrush"]!)
                .Color.Should().Be(Color.Parse("#aa00aa"));

            probe.TryFindResource(
                "SystemControlForegroundBaseMediumLowBrush", ThemeVariant.Dark, out var fromChain)
                .Should().BeTrue("the live chain keeps serving the family");
            ((SolidColorBrush)fromChain!).Color.Should().Be(Color.Parse("#a8a8a8"),
                "the second apply retints the derived family in the same running world");
        });
    }

    /// <summary>Register one theme file (the off-thread half: files and the
    /// manager's registry, no dispatcher surfaces) and return the manager.</summary>
    private static async Task<ThemeManager> RegisterTheme(
        string name,
        IReadOnlyDictionary<string, string> colors)
    {
        var dir = Directory.CreateTempSubdirectory("zt-derived-publish-");
        try
        {
            var file = Path.Combine(dir.FullName, $"{name}.json");
            await File.WriteAllTextAsync(
                file,
                JsonSerializer.Serialize(new ThemeDefinition
                {
                    Name = name,
                    Colors = new Dictionary<string, string>(colors),
                }));

            var manager = new ThemeManager(
                NullLogger<ThemeManager>.Instance,
                dir.FullName);
            await manager.LoadCustomThemeAsync(file);

            return manager;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Post one drain job on the dispatcher's owner thread; the
    /// pump's protocol surfaces any failure the job throws.</summary>
    private static async Task Run(Action job)
    {
        Dispatcher.UIThread.Post(job);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>
    /// Materialize the headless application world once: a bare Avalonia
    /// application (Fluent hosted like the product's App.axaml) riding the
    /// pump thread, plus a probe window already in the resource chain. Later
    /// facts reuse it — the app is the process-singleton resource world —
    /// and a double setup (another class's world first) reuses the existing
    /// application instead of failing the builder.
    /// </summary>
    private static async Task<(Application App, Window Probe)> EnsureAppWindow()
    {
        _appWindowTask ??= SetupAppWindow();
        return await _appWindowTask;
    }

    private static async Task<(Application App, Window Probe)> SetupAppWindow()
    {
        Application? app = null;
        Window? probe = null;
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
                        .SetupWithoutStarting();
                    app = Application.Current;
                }

                // The facts' own precondition, in every world (another test
                // class may have stood the world up first): Fluent hosts the
                // stock system family the publish converges — exactly what
                // the product's App.axaml carries.
                app!.RequestedThemeVariant = ThemeVariant.Dark;
                if (app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any() is false)
                {
                    app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                }

                probe = new Window { Content = new Border(), Width = 10, Height = 10 };
                probe.Show();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        await AvaloniaDispatcherPump.DrainAsync();

        if (failure is not null)
        {
            throw failure;
        }

        app.Should().NotBeNull("the headless world carries an application");
        probe.Should().NotBeNull("the probe window rides the application chain");
        return (app!, probe!);
    }
}
