namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using ZettaTrade.App.Tests;

/// <summary>
/// The surface-stray pins (T-182): the small surfaces ride the T-155 scale
/// and the palette — no off-scale spacing component remains anywhere the
/// App ships (the review's drift set mapped onto the scale steps, riding
/// the tokens the T-155 adoption pinned), the overlay drop anchors are the
/// documented exception (position, not spacing), no bare numeric Opacity
/// rides any view (the dimmers moved onto the palette's dim ink, a named
/// state class and the derived scrim veil), the dimmed elements read in
/// every shipped theme (the 4.5 bar over both inks they sit on), and the
/// scrim veil derives from the applied Background on the real apply path.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class SurfaceStrayPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    private static readonly string ThemesDirectory =
        Path.Combine(RepoRoot, "themes");

    /// <summary>Every spacing-family attribute, its value captured.</summary>
    private static readonly Regex SpacingAttribute = new(
        "(Margin|Padding|Spacing|RowSpacing|ColumnSpacing)=\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>The scale the T-155 tokens publish — the components a
    /// multi-value spacing attribute may ride.</summary>
    private static readonly int[] ScaleSteps = [4, 8, 12, 16, 24];

    /// <summary>The components a multi-value attribute may carry: the scale
    /// plus zero (the scale's own zero has no token, and inside a
    /// multi-component value zero is position, never drift).</summary>
    private static readonly int[] LegalComponents = [0, .. ScaleSteps];

    /// <summary>
    /// The position anchors: values that place elements relative to the
    /// chrome they hang under or compensate a glyph's line box — position,
    /// not spacing — and a scale step cannot express them. The pin names
    /// each with its role; anything else off-scale is drift.
    /// </summary>
    private static readonly string[] AnchorMargins =
    [
        "0,44,12,0", // the toasts' drop below the title strip
        "0,72,0,0",  // the palette's and the hotkeys sheet's drop
        "0,52,12,0", // the hotkeys sheet's content card drop
        "0,0,0,40",  // the log viewer's overlay-bar clearance
        "-13,0,0,0", // the rail tick's pull onto the item's own left edge (the item border 1 + padding 12, T-222)
        "0,-2,0,-2", // the pair-star's vertical nudge under the glyph's line box (T-152)
    ];

    private static Task<(Application App, Window Probe)>? _appWindowTask;

    static SurfaceStrayPinsTests()
    {
        AvaloniaDispatcherPump.EnsureInstalled();
    }

    [Fact]
    public void EverySpacingAttribute_RidesTheScale()
    {
        var files = Directory
            .EnumerateFiles(AppProjectDirectory, "*.axaml", SearchOption.AllDirectories)
            .ToList();

        files.Should().HaveCountGreaterThan(
            20,
            "the walk covers the whole App project (a shrunk walk is a false green)");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in SpacingAttribute.Matches(source))
            {
                var value = match.Groups["value"].Value;
                var name = match.Groups[1].Value;
                var line = source[..match.Index].Count(c => c == '\n') + 1;

                // The token and binding forms ride published or dynamic
                // values — the scale's adoption path.
                if (value.Contains('{', StringComparison.Ordinal))
                {
                    continue;
                }

                // The bare zero: the scale's own zero.
                if (value == "0")
                {
                    continue;
                }

                // The overlay drop anchors: position, not spacing.
                if (AnchorMargins.Contains(value, StringComparer.Ordinal))
                {
                    continue;
                }

                var parts = value.Split('.', ',');
                if (parts.Length == 1)
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)}:{line}: {name}=\"{value}\" — a bare literal " +
                        "is an unadopted token or an off-scale drift (T-155/T-182)");
                    continue;
                }

                var off = parts.Where(part =>
                    !int.TryParse(part, out var step) || !LegalComponents.Contains(step));
                foreach (var part in off)
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)}:{line}: {name}=\"{value}\" — the component " +
                        $"{part} sits off the T-155 scale");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every spacing value rides the scale or its token: {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void NoBareOpacityLiteral_RidesTheViews()
    {
        foreach (var source in Directory.GetFiles(
                     AppProjectDirectory, "*.axaml", SearchOption.AllDirectories)
                     .Select(File.ReadAllText))
        {
            Regex.Matches(source, "Opacity=\"[0-9]")
                .Should().BeEmpty(
                    "a bare numeric Opacity is an untuned dim state — the dimmers ride " +
                    "the palette's dim ink, a named state class or the derived veil");
        }
    }

    [Fact]
    public void TheDimmers_RideThePaletteRoles()
    {
        var main = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "MainWindow.axaml"));
        var ticket = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "NewOrderDialogWindow.axaml"));
        var chart = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "Panels", "ChartPanelView.axaml"));

        // The watchlist row's 24h volume: the dim ink, no opacity veil.
        var volume = BlockOf(main, "<TextBlock Text=\"{Binding Volume24hText}\"");
        volume.Should().Contain(
            "Foreground=\"{DynamicResource TextSecondary}\"",
            "the volume reads through the palette's dim ink");
        volume.Should().NotContain("Opacity", "the veil is gone — the ink is the dim state");

        // The ticket's sizer support texts: the same dim ink.
        foreach (var marker in new[]
                 {
                     "<TextBlock Text=\"{loc:Localized NewOrder.SizerRiskHint}\"",
                     "<TextBlock Text=\"{loc:Localized NewOrder.SizerSnapshotAbsent}\"",
                     "<TextBlock Text=\"{Binding Sizer.SnapshotDetail}\"",
                 })
        {
            BlockOf(ticket, marker).Should().Contain(
                "Foreground=\"{DynamicResource TextSecondary}\"",
                "the {0} support text reads through the palette's dim ink", marker);
        }

        // The chart toolbar's overlay toggles: the quiet class ships its own
        // .on sibling (the window-scoped rest would defeat the app-scoped
        // kit style — the T-181 mechanism), and neither block paints a
        // Background (the generic hover and focus stay alive).
        chart.Should().Contain(
            "Classes=\"overlay-toggle\"",
            "the two overlay toggles carry the named dim class");
        var rest = BlockOf(chart, "<Style Selector=\"Button.overlay-toggle\">");
        rest.Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextSecondary}\" />",
            "the toggles' rest reads the dim ink");
        rest.Should().NotContain(
            "Property=\"Background\"",
            "the class never paints — the generic hover/focus stay alive");
        var armed = BlockOf(chart, "<Style Selector=\"Button.overlay-toggle.on\">");
        armed.Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource Highlight}\" />",
            "the armed toggle answers in the kit's on-ink");
        chart.IndexOf(armed, StringComparison.Ordinal).Should().BeGreaterThan(
            chart.IndexOf(rest, StringComparison.Ordinal),
            "the armed sibling rides after the rest — the on-state wins the ink race");
    }

    [Fact]
    public void TheScrims_RideTheDerivedVeil()
    {
        var main = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "MainWindow.axaml"));

        foreach (var scrim in new[] { "PaletteScrim", "HotkeysSheetOverlay" })
        {
            var block = BlockOf(main, $"x:Name=\"{scrim}\"");
            block.Should().Contain(
                "Background=\"{DynamicResource ScrimBrush}\"",
                "the {0} veil rides the derived scrim brush", scrim);
            block.Should().NotContain(
                "Opacity",
                "the {0}'s translucency lives in the brush, never a bare literal", scrim);
        }
    }

    [Fact]
    public void EveryShippedTheme_ReadsTheDimInk()
    {
        // The acceptance's contrast bar: the dimmed elements (the dim-ink
        // texts) read at or above 4.5:1 over both inks they sit on — the
        // window background at rest and the surface the rows ride — in
        // every shipped theme.
        foreach (var file in Directory.EnumerateFiles(ThemesDirectory, "*.json"))
        {
            var palette = JsonDocument.Parse(File.ReadAllText(file))
                .RootElement.GetProperty("colors");
            var dim = palette.GetProperty("TextSecondary").GetString()!;
            var background = palette.GetProperty("Background").GetString()!;
            var surface = palette.GetProperty("Surface").GetString()!;

            var overBackground = Contrast.Ratio(dim, background);
            overBackground.Should().BeGreaterThanOrEqualTo(
                4.5, "{0}: the dim ink over the background reads {1:F2}:1",
                Path.GetFileName(file), overBackground);

            var overSurface = Contrast.Ratio(dim, surface);
            overSurface.Should().BeGreaterThanOrEqualTo(
                4.5, "{0}: the dim ink over the surface reads {1:F2}:1",
                Path.GetFileName(file), overSurface);
        }
    }

    [Fact]
    public void TheVeilDerivation_KeepsTheSourceRgbAndTheVeilAlpha()
    {
        var veiled = ScrimBrushPublisher.Derive(Color.Parse("#0a0a0f"));

        veiled.R.Should().Be(0x0a, "the veil keeps the source red");
        veiled.G.Should().Be(0x0a, "the veil keeps the source green");
        veiled.B.Should().Be(0x0f, "the veil keeps the source blue");
        veiled.A.Should().Be(ScrimBrushPublisher.VeilAlpha,
            "the veil's alpha is the 0.75 wash the pre-T-182 sites composited");
    }

    [Fact]
    public async Task EveryShippedTheme_ServesTheVeilFromItsOwnBackground()
    {
        // The real apply path: each apply publishes the source keys as
        // Colors (the T-150 shape) and the publisher answers with the
        // "ScrimBrush" the scrims ride — the applied Background's RGB under
        // the veil alpha, retinted live on every re-apply.
        var player = Directory.CreateTempSubdirectory("zt-scrim-");
        try
        {
            var manager = new ThemeManager(
                NullLogger<ThemeManager>.Instance,
                player.FullName,
                ThemesDirectory);
            await manager.InitializeAsync();

            var publisher = new ScrimBrushPublisher(
                manager,
                NullLogger<ScrimBrushPublisher>.Instance);
            await publisher.InitializeAsync();

            var (app, _) = await EnsureAppWindow();

            await Run(() =>
            {
                foreach (var theme in new[] { "dark", "light", "midnight", "oled", "trading-pro" })
                {
                    manager.ApplyThemeAsync(theme).GetAwaiter().GetResult();

                    var background = (Color)app.Resources["Background"]!;
                    var veil = ((SolidColorBrush)app.Resources[ScrimBrushPublisher.ResourceKey]!).Color;
                    veil.Should().Be(
                        ScrimBrushPublisher.Derive(background),
                        "the {0} apply veils its own background", theme);
                }
            });
        }
        finally
        {
            player.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The full element (or style) block carrying the marker: from the tag
    /// start preceding the marker through the matching close. The first
    /// bracket cannot serve — attributes and setters ride across several
    /// lines, so the block must survive the whole open tag.
    /// </summary>
    private static string BlockOf(string source, string marker)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"the marker must exist: {marker}");
        var start = source.LastIndexOf('<', markerIndex);
        start.Should().BeGreaterThanOrEqualTo(0, $"the marker must live inside a tag: {marker}");

        var close = source.IndexOf('>', start);
        close.Should().BeGreaterThan(0, $"the marker's tag must close: {marker}");
        if (source[close - 1] == '/')
        {
            return source[start..(close + 1)];
        }

        var depth = 1;
        var scan = close + 1;
        while (depth > 0 && scan < source.Length)
        {
            var tag = source.IndexOf('<', scan);
            if (tag < 0)
            {
                break;
            }

            if (source[tag + 1] == '!')
            {
                scan = source.IndexOf("-->", tag, StringComparison.Ordinal) + 3;
                continue;
            }

            var tagClose = source.IndexOf('>', tag);
            if (source[tag + 1] == '/')
            {
                depth--;
            }
            else if (source[tagClose - 1] != '/')
            {
                depth++;
            }

            scan = tagClose + 1;
        }

        depth.Should().Be(0, $"the marker's element must close: {marker}");
        return source[start..scan];
    }

    /// <summary>Post one drain job on the dispatcher's owner thread; the
    /// pump's protocol surfaces any failure the job throws.</summary>
    private static async Task Run(Action job)
    {
        Dispatcher.UIThread.Post(job);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>
    /// The headless application world (the process singleton the chip
    /// foreground facts share): reuse over re-setup, Fluent hosted like the
    /// product's App.axaml, a probe window keeping the chain warm.
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
                        .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions())
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

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker: tests, gate runners and IDE runs all execute inside
    /// the repository, so this resolves identically for every consumer.
    /// </summary>
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
