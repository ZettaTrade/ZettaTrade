namespace ZettaTrade.App.Tests.Views;

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
using ZettaTrade.App.Services;

/// <summary>
/// The readable-chip foreground facts (T-152): the derivation picks the
/// achromatic candidate whose worst pair reads best — near-black over the
/// dark themes' bright state fills, near-white over the light theme's
/// darker ones (no fixed foreground clears WCAG on both worlds: white over
/// #22c55e measures ~2.3:1, black over the light fills ~3.8:1) — and every
/// shipped theme's real apply publishes that foreground into the resource
/// world, where the chip templates ride it. The ratio measure runs through
/// Livery's public <see cref="Contrast.Ratio"/> — the battery's own math,
/// so a drift in the engine invalidates these pins loudly. The world is
/// dispatcher-affine (the shared process-singleton application the
/// derived-system-key facts stand up): the apply and every assertion run
/// inside posted drain jobs, a never-drained job never executes.
/// </summary>
public sealed class ChipForegroundPublishTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static Task<(Application App, Window Probe)>? _appWindowTask;

    static ChipForegroundPublishTests()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
    }

    [Fact]
    public void TheBrightStateColors_ReadWithTheDarkForeground()
    {
        // The dark-family fills the four dark themes share (the lane chip's
        // paper/testnet/live classes): black clears the 4.5 pill bar on
        // every one of them, white fails it on two.
        var fills = new[] { "#22c55e", "#ffd700", "#ef4444" };

        ChipForegroundPublisher.ChooseForeground(fills)
            .Should().Be("#000000", "the bright fills read best with the dark extreme");

        foreach (var fill in fills)
        {
            Contrast.Ratio("#000000", fill)
                .Should().BeGreaterThanOrEqualTo(4.5, "black over {0} must clear the pill bar", fill);
        }
    }

    [Fact]
    public void TheLightStateColors_ReadWithTheWhiteForeground()
    {
        // The light theme ships darker fills for the very same keys: the
        // dark extreme collapses there (black over #8a6100 reads 3.79:1),
        // white clears on every fill.
        var fills = new[] { "#15803d", "#8a6100", "#dc2626" };

        ChipForegroundPublisher.ChooseForeground(fills)
            .Should().Be("#ffffff", "the darker fills read best with the light extreme");

        foreach (var fill in fills)
        {
            Contrast.Ratio("#ffffff", fill)
                .Should().BeGreaterThanOrEqualTo(4.5, "white over {0} must clear the pill bar", fill);
        }
    }

    [Fact]
    public void APalettePairingBrightAndDarkFills_KeepsTheBetterCandidate()
    {
        // The documented best-effort edge: a user palette can pair a bright
        // and a dark chip fill, denying both achromatic candidates the 4.5
        // bar somewhere. The derivation keeps the better worst-case (white:
        // its worst pair reads 2.28:1 where black's reads 1.10:1) rather
        // than giving up — the shipped themes' own battery pins that set.
        ChipForegroundPublisher.ChooseForeground(new[] { "#22c55e", "#101010" })
            .Should().Be("#ffffff", "the better of the two worst cases wins");
    }

    [Fact]
    public async Task EveryShippedTheme_ServesAReadableChipForeground()
    {
        // The acceptance battery: the real theme files walk the real
        // apply path — each apply publishes its fills (source keys as
        // Colors, the T-150 shape) and the publisher answers with the
        // "ChipForeground" brush the chips ride; the rendered pairs all
        // measure at or above the 4.5:1 pill bar.
        var player = Directory.CreateTempSubdirectory("zt-chip-fg-");
        try
        {
            var manager = new ThemeManager(
                NullLogger<ThemeManager>.Instance,
                player.FullName,
                Path.Combine(RepoRoot, "themes"));
            await manager.InitializeAsync();

            var publisher = new ChipForegroundPublisher(
                manager,
                NullLogger<ChipForegroundPublisher>.Instance);
            await publisher.InitializeAsync();

            var (app, _) = await EnsureAppWindow();

            await Run(() =>
            {
                foreach (var (theme, expectedFg) in new (string Theme, string Fg)[]
                         {
                             ("dark", "#000000"),
                             ("light", "#ffffff"),
                             ("midnight", "#000000"),
                             ("oled", "#000000"),
                             ("trading-pro", "#000000"),
                         })
                {
                    manager.ApplyThemeAsync(theme).GetAwaiter().GetResult();

                    var fg = ((SolidColorBrush)app.Resources[ChipForegroundPublisher.ResourceKey]!)
                        .Color;
                    fg.Should().Be(
                        Color.Parse(expectedFg),
                        "the {0} apply must publish the readable candidate", theme);

                    foreach (var fillKey in new[] { "CandleUp", "Highlight", "Error" })
                    {
                        var fill = (Color)app.Resources[fillKey]!;
                        var ratio = Contrast.Ratio(Hex(fg), Hex(fill));
                        ratio.Should().BeGreaterThanOrEqualTo(
                            4.5,
                            "the {0} chip text over the applied {1} fill reads {2:F2}:1",
                            theme,
                            fillKey,
                            ratio);
                    }
                }
            });
        }
        finally
        {
            player.Delete(recursive: true);
        }
    }

    /// <summary>The fill hex a publish consumes: the resource world's
    /// Color, re-encoded exactly the way the publisher derives.</summary>
    private static string Hex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Post one drain job on the dispatcher's owner thread; the
    /// pump's protocol surfaces any failure the job throws.</summary>
    private static async Task Run(Action job)
    {
        Dispatcher.UIThread.Post(job);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>
    /// The headless application world, shared with the derived-system-key
    /// facts (the process singleton): reuse over re-setup, Fluent hosted
    /// like the product's App.axaml, a probe window keeping the chain warm.
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

    /// <summary>Walk up from the test's base directory to the folder
    /// carrying the solution marker (the palette battery's root walk).</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZettaTrade.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run inside a ZettaTrade repository checkout");
        return directory!.FullName;
    }
}
