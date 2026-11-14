namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The transfer chip's live facts (T-235's review round): the REAL settings
/// window materializes over a REAL SettingsViewModel exactly as the main
/// window's open path builds it (the bare window's failed command bindings
/// turned the chips effectively disabled — a binding-error artifact, not
/// the app's shape, and it masked every pointer fact), the pair lists
/// repaint from the stubbed exchange's listing through the open reset, and
/// the composition is MEASURED against the approved mockup: the chevron
/// ink centers (the data-origin geometry left it in the corner), the rest
/// fill clears the mockup's plate contrast over the plane the chip
/// ACTUALLY sits on — the section card's Surface, the plane both review
/// rounds missed (the wells' 3% step measured 1.08:1 there and read as
/// nothing), the enabled chip's pointer answers with the Highlight stroke
/// (the mockup's gold ring) while the fill holds, and the gate-disabled
/// chip (no pick yet — the move actions need one) reads asleep and never
/// arms the hover: the full-bright chip that answers nothing was the
/// broken-hover read the review caught.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessTransferChipTests
{
    static HeadlessTransferChipTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The approved v2 mockup's plate contrast (#222433 on #14151f ≈ 1.18):
    /// the floor the rest fill must clear over the plane the chip really
    /// sits on — the mockup round's own read is the contract.
    /// </summary>
    private const double TheApprovedPlateContrastFloor = 1.18;

    /// <summary>The gate-disabled chip's dim step: the asleep read.</summary>
    private const double TheAsleepDimStep = 0.55;

    [Fact]
    public async Task TheChevronInk_SitsAtTheChipCenter()
    {
        var (window, hide, _) = await ChipsAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var path = HeadlessWorld.Descendants<Path>(hide).Single();
                path.RenderedGeometry.Should().NotBeNull("the chevron carries its stroke geometry");

                var ink = path.RenderedGeometry!.Bounds;
                var inkCenter = path.TranslatePoint(
                    new Point(ink.X + (ink.Width / 2.0), ink.Y + (ink.Height / 2.0)), hide)!.Value;
                var chipCenter = new Point(hide.Bounds.Width / 2.0, hide.Bounds.Height / 2.0);

                inkCenter.X.Should().BeApproximately(chipCenter.X, 1.0,
                    "the chevron ink centers horizontally — the ink measured at {0:F1} against the chip center {1:F1}",
                    inkCenter.X, chipCenter.X);
                inkCenter.Y.Should().BeApproximately(chipCenter.Y, 1.0,
                    "the chevron ink centers vertically — the ink measured at {0:F1} against the chip center {1:F1}",
                    inkCenter.Y, chipCenter.Y);
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheRestFill_ClearsTheApprovedPlateContrastOverItsPlane()
    {
        var (window, hide, _) = await ChipsAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var fill = Solid(Presenter(hide).Background, "the chip's rest fill");
                var card = hide.GetSelfAndVisualAncestors().OfType<Border>()
                    .First(border => border.Classes.Contains("section-card"));
                var plane = Solid(card.Background, "the section card's ground");

                FillOver(fill, plane).Should().BeGreaterThanOrEqualTo(
                    TheApprovedPlateContrastFloor,
                    "the plate reads over the card like the approved mockup did — the fill {0} over the plane {1} measured {2:F2}",
                    fill, plane, FillOver(fill, plane));
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheEnabledChip_AnswersThePointerWithTheHighlightStroke()
    {
        var (window, hide, _) = await ChipsAsync();
        try
        {
            var restFill = await HeadlessWorld.OnUi(() =>
                Solid(Presenter(hide).Background, "the chip's rest fill"));

            // Sweep the pointer onto the chip: the state arms through the
            // same input path a user's mouse feeds.
            await HeadlessWorld.OnUi(() => window.MouseMove(CenterOf(window, hide)));
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                hide.IsPointerOver.Should().BeTrue(
                    "the pointer rides the enabled chip (the classes carry {0})", string.Join(",", hide.Classes));
                hide.Classes.Should().Contain(":pointerover",
                    "the pointerover state arms the chip's hover look");

                var presenter = Presenter(hide);
                var stroke = Solid(presenter.BorderBrush, "the chip's hover stroke");
                window.TryFindResource("Highlight", out var gold).Should().BeTrue(
                    "the armed ink's token resolves in the applied palette");
                stroke.Should().Be(ResourceColor(gold!, "the Highlight token"),
                    "the hover answers with the Highlight stroke — the mockup's gold ring (the stroke measured {0})",
                    stroke);
                Solid(presenter.Background, "the chip's hover fill").Should().Be(restFill,
                    "the hover keeps the plate fill (the generic tint never thins it out)");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheGateDisabledChip_ReadsAsleep_AndNeverArmsTheHover()
    {
        var (window, _, reveal) = await ChipsAsync();
        try
        {
            // Sweep the pointer onto the disabled chip the way a user's
            // session would: the gate is shut (the hidden list holds no
            // pick), so the chip must read asleep and stay silent.
            await HeadlessWorld.OnUi(() => window.MouseMove(CenterOf(window, reveal)));
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                reveal.Classes.Should().Contain(":disabled",
                    "the reveal gate is shut while the hidden list holds no pick (the classes carry {0})",
                    string.Join(",", reveal.Classes));
                reveal.Opacity.Should().Be(TheAsleepDimStep,
                    "the asleep chip dims to the asleep read — a full-bright chip that answers nothing is the broken-hover read the review caught");
                reveal.IsPointerOver.Should().BeFalse(
                    "the pointer never arms a gate-disabled chip");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The chips on their real layout: the settings window materializes
    /// over a REAL ViewModel (the main window's open path — the bare
    /// window's failed command bindings disabled the chips), the reset
    /// repaints the pair lists from the stubbed exchange's listing, the
    /// pick arms the hide chip, the tab rail walks until the pairs section
    /// builds, and the two framed chips answer.
    /// </summary>
    private static async Task<(SettingsWindow Window, Button Hide, Button Reveal)> ChipsAsync()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        var window = await HeadlessWorld.OnUi(() =>
            new SettingsWindow { DataContext = ComposeViewModel() });
        await HeadlessWorld.ShowAndSettleAsync(window);

        // The open path the main window rides: the reset repaints the pair
        // lists from the listing (the chip's hide gate counts the rows).
        var reset = await HeadlessWorld.OnUi(() =>
            ((SettingsViewModel)window.DataContext!).ResetSelectionsAsync());
        await reset;
        await AvaloniaDispatcherPump.DrainAsync();

        // The transfer actions act on a pick: pick the first unstarred row
        // so the hide chip rides its ENABLED composition; the reveal chip
        // stays gate-disabled (the hidden list starts empty).
        await HeadlessWorld.OnUi(() =>
        {
            var viewModel = (SettingsViewModel)window.DataContext!;
            viewModel.SelectedAllPair = viewModel.AllPairs.First(pair => !pair.IsFavorite);
        });
        await AvaloniaDispatcherPump.DrainAsync();

        Button? hide = null;
        Button? reveal = null;
        for (var attempt = 0; attempt < 8 && (hide is null || reveal is null); attempt++)
        {
            (hide, reveal) = await HeadlessWorld.OnUi(() =>
            {
                var framed = HeadlessWorld.Descendants<Button>(window)
                    .Where(candidate =>
                        candidate.Classes.Contains("icon") && candidate.Classes.Contains("framed"))
                    .ToList();
                return (framed.FirstOrDefault(), framed.Skip(1).FirstOrDefault());
            });
            if (hide is not null && reveal is not null)
            {
                break;
            }

            // The tab rail virtualizes: walk the tabs by index (a
            // first-unselected walk would oscillate 0<->1 and unload the
            // chip's home on the pass after it built).
            var index = attempt;
            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                if (index < tabs.Count)
                {
                    tabs[index].IsSelected = true;
                }
            });
        }

        hide.Should().NotBeNull(
            "the hide chip rides the settings' pairs section — {0}", await HeadlessWorld.OnUi(() => Dump(window)));
        reveal.Should().NotBeNull(
            "the reveal chip rides beside the hide chip — {0}", await HeadlessWorld.OnUi(() => Dump(window)));

        return (window, hide!, reveal!);
    }

    /// <summary>The materialized tree's shape, for a failed walk's message.</summary>
    private static string Dump(Window window) =>
        string.Join("; ",
            $"windowSize={window.Bounds.Width:0}x{window.Bounds.Height:0}",
            $"tabItems={HeadlessWorld.Descendants<TabItem>(window).Count()}",
            $"buttons={HeadlessWorld.Descendants<Button>(window).Count()}",
            $"framed={HeadlessWorld.Descendants<Button>(window).Count(candidate =>
                candidate.Classes.Contains("icon") && candidate.Classes.Contains("framed"))}");

    /// <summary>
    /// Compose a settings ViewModel exactly as the rail render pins and the
    /// external-servers suite do: the REAL localization singleton behind the
    /// loc markup, a stubbed exchange whose listing feeds the pair lists,
    /// and the manager double the rows read.
    /// </summary>
    private static SettingsViewModel ComposeViewModel()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("midnight");
        themeManager.AvailableThemes.Returns([new("midnight", "Midnight Blue", true, [])]);

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        adapter.Name.Returns("Demo");
        adapter.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyCollection<Symbol>(
            [
                Symbol.Parse("BTC/USDT"),
                Symbol.Parse("ETH/USDT"),
                Symbol.Parse("ETH/BTC"),
                Symbol.Parse("XRP/USDT"),
            ]));
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            settings,
            NullLogger<ActiveExchangeService>.Instance);

        return new SettingsViewModel(
            settings,
            themeManager,
            LocalizationManager.Instance,
            Substitute.For<IShellUtils>(),
            new McpHostService(
                settings,
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            new SymbolListProvider(activeExchanges, NullLogger<SymbolListProvider>.Instance),
            CatalogOf(),
            new ManagerDouble(),
            NullLogger<SettingsViewModel>.Instance);
    }

    /// <summary>The catalog stub reporting no plugins loaded (the pairs tab is indifferent).</summary>
    private static IIndicatorCatalog CatalogOf()
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns([]);
        return catalog;
    }

    /// <summary>The hand-written manager double: the rows read its empty snapshot.</summary>
    private sealed class ManagerDouble : IMcpClientManager
    {
        public IReadOnlyList<McpClientState> States { get; } = [];

        public IObservable<IReadOnlyList<McpClientState>> StatesChanged { get; } =
            new Subject<IReadOnlyList<McpClientState>>();

        public Task ApplyAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ContentPresenter Presenter(Button button) =>
        HeadlessWorld.Descendants<ContentPresenter>(button)
            .First(presenter => presenter.Name == "PART_ContentPresenter");

    private static Color Solid(IBrush? brush, string lane) =>
        (brush as ISolidColorBrush)?.Color
            ?? throw new InvalidOperationException(
                $"the {lane} resolved to {(brush?.ToString() ?? "null")} — the DynamicResource never landed");

    /// <summary>
    /// The palette resource's color: the roles publish as raw Colors (the
    /// ThemeManager's own mapping), the derived families as brushes — a
    /// fact reads either shape.
    /// </summary>
    private static Color ResourceColor(object? resource, string lane) => resource switch
    {
        Color color => color,
        ISolidColorBrush brush => brush.Color,
        _ => throw new InvalidOperationException(
            $"the {lane} resolved to {resource?.GetType().Name ?? "null"} — the palette never landed"),
    };

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2.0, control.Bounds.Height / 2.0), window)!.Value;

    /// <summary>The WCAG contrast ratio of one fill over another (the mockup round's own math).</summary>
    private static double FillOver(Color over, Color ground)
    {
        static double Channel(byte raw)
        {
            var scaled = raw / 255.0;
            return scaled <= 0.04045 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        var lighter = Math.Max(Luminance(over), Luminance(ground));
        var darker = Math.Min(Luminance(over), Luminance(ground));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
