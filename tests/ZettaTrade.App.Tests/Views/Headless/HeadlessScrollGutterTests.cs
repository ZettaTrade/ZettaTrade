namespace ZettaTrade.App.Tests.Views.Headless;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Livery.Localization;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.App.Views.Panels;

/// <summary>
/// The headless scroll-gutter pins (T-170): the REAL windows materialize
/// headlessly and every audited scroll surface's gutter is MEASURED, not
/// source-pinned — the Fluent overlay scrollbar's 16 px strip must never
/// ride over the content it appears over. The facts read the view's own
/// declared geometry, so the windows materialize WITHOUT a view model:
/// the gutters are the views' properties, not the VMs', and every
/// window's activation body is null-safe (the <c>ViewModel?.</c> guards
/// skip the plumbing) — the doc comments say what the facts measure so
/// the scope stays honest. Every window touch rides the world's
/// user-interface lane (an off-thread constructor dies on VerifyAccess);
/// the facts share the LocalizationManager singleton, so the class rides
/// the LocalizationManagerSingleton collection. The T-170 sibling T-169
/// shipped the same 16 px shape for the settings tabs; these pins hold
/// the rest of the app to it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessScrollGutterTests
{
    static HeadlessScrollGutterTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The dialog windows' outer scroll contents end a 16 px strip short
    /// of the overlay bar's right edge: the alerts list (a ListBox under
    /// the outer bar — the audit's direct overlap report), the balances
    /// and order-history stacks, the notification summary and the
    /// license-activation terms.
    /// </summary>
    [Fact]
    public async Task TheDialogWindows_OuterScrollContents_ClearTheOverlayBarStrip()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        await AssertEveryScrollContentClearsTheBar<AlertsWindow>("the alerts list rides the outer bar");
        await AssertEveryScrollContentClearsTheBar<BalancesPanelWindow>("the balances stack ends a strip short");
        await AssertEveryScrollContentClearsTheBar<OrderHistoryWindow>("the history stack ends a strip short");
        await AssertEveryScrollContentClearsTheBar<NotificationHistoryWindow>("the summary must not sit under the bar");
        await AssertEveryScrollContentClearsTheBar<LicenseActivationWindow>("the terms wrap under the bar");

        async Task AssertEveryScrollContentClearsTheBar<TWindow>(string because)
            where TWindow : Window, new()
        {
            var window = await HeadlessWorld.OnUi(() => new TWindow());
            await HeadlessWorld.ShowAndSettleAsync(window);
            try
            {
                await HeadlessWorld.OnUi(() =>
                    ScrollContents(window).Should().NotBeEmpty(because)
                        .And.OnlyContain(
                            content => content.Margin.Right == 16,
                            "the content ends a strip short of the overlay bar"));
            }
            finally
            {
                await HeadlessWorld.CloseAndSettleAsync(window);
            }
        }
    }

    /// <summary>
    /// The positions panel's three tab stacks (positions, open orders,
    /// recent orders) end a 16 px strip short of the overlay bar — the
    /// row actions must not sit under it. The TabControl materializes one
    /// tab's content at a time, so the fact walks the three headers the
    /// same way a user does.
    /// </summary>
    [Fact]
    public async Task ThePositionsPanelTabs_StacksClearTheOverlayBarStrip()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();
        var window = await HeadlessWorld.OnUi(() => new PositionsPanelWindow());
        await HeadlessWorld.ShowAndSettleAsync(window);
        try
        {
            foreach (var key in new[] { "Positions.TabPositions", "Positions.TabOpenOrders", "Positions.TabRecent" })
            {
                await SelectTabAsync(window, key);
                await HeadlessWorld.OnUi(() =>
                    ScrollContents(window).Should().HaveCount(1, $"the {key} tab carries one audited stack")
                        .And.OnlyContain(
                            content => content.Margin.Right == 16,
                            "the tab stack ends a strip short of the overlay bar"));
            }
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The main window's internal-bar lists clear their own overlay bars
    /// (the watchlist and the palette list carry the 16 px strip as their
    /// internal list padding), and the assistant chat's content carries
    /// the bar's strip on top of its 8 px insets — 24 px on the right.
    /// The facts ride the full main world: the lists render real rows
    /// inside the real window.
    /// </summary>
    [Fact]
    public async Task TheMainWindowLists_AndTheAssistantChat_ClearTheirOverlayBars()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var watchRows = HeadlessWorld.Descendants<ListBox>(window)
                    .Single(list => list.Classes.Contains("watch-rows"));
                watchRows.Padding.Right.Should().Be(16, "the watch rows clear the list's own overlay bar");

                var palette = HeadlessWorld.Descendants<ListBox>(window)
                    .Single(list => list.Name == "PaletteRows");
                palette.Padding.Right.Should().Be(16, "the palette rows clear the list's own overlay bar");

                var chat = HeadlessWorld.Descendants<AssistantPanelView>(window)
                    .SelectMany(panel => panel.GetVisualDescendants().OfType<ScrollViewer>())
                    .Select(viewer => viewer.Content)
                    .OfType<ItemsControl>()
                    .ToList();
                chat.Should().NotBeEmpty("the assistant chat is one scrolled ItemsControl");
                foreach (var content in chat)
                {
                    content.Margin.Should().Be(
                        new Thickness(8, 8, 24, 8),
                        "the chat content carries the bar's strip over its own insets");
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The wizard's symbol list and the log viewer's rows scroll inside
    /// their own viewports, so the gutter rides the list's internal
    /// padding — each window carries exactly one audited list.
    /// </summary>
    [Fact]
    public async Task TheWizardSymbols_AndTheLogRows_ClearTheirInternalBars()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        await AssertItsSingleListClearsTheBar<FirstRunWizardWindow>("the symbol list scrolls inside its own viewport");
        await AssertItsSingleListClearsTheBar<LogViewerWindow>("the log rows scroll inside their own viewport");

        async Task AssertItsSingleListClearsTheBar<TWindow>(string because)
            where TWindow : Window, new()
        {
            var window = await HeadlessWorld.OnUi(() => new TWindow());
            await HeadlessWorld.ShowAndSettleAsync(window);
            try
            {
                await HeadlessWorld.OnUi(() =>
                    HeadlessWorld.Descendants<ListBox>(window).Should().HaveCount(1, because)
                        .And.OnlyContain(
                            list => list.Padding.Right == 16,
                            "the rows end a strip short of the list's own overlay bar"));
            }
            finally
            {
                await HeadlessWorld.CloseAndSettleAsync(window);
            }
        }
    }

    /// <summary>
    /// Every scroll surface whose CONTENT is a top-level panel or list:
    /// the template internals (a list box's own viewport part) carry other
    /// content shapes and are excluded by construction.
    /// </summary>
    private static IReadOnlyList<Control> ScrollContents(Window window) =>
        HeadlessWorld.Descendants<ScrollViewer>(window)
            .Select(viewer => viewer.Content as Control)
            .Where(content => content is TextBlock or StackPanel or ItemsControl)
            .Select(content => content!)
            .ToList();

    /// <summary>
    /// Select one tab by its localization key and land the switch on the
    /// serialized dispatcher — the selected tab's content materializes
    /// only in its lane, so the drain is what makes the facts measurable.
    /// </summary>
    private static async Task SelectTabAsync(Window window, string headerKey)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var expected = LocalizationManager.Instance.GetString(headerKey);
            HeadlessWorld.Descendants<TabItem>(window)
                .Single(tab => tab.Header as string == expected)
                .IsSelected = true;
        });
    }

    /// <summary>Materialize the main window at the shipped default size and land its open.</summary>
    private static async Task<MainWindow> ShowMainWindowAsync(MainViewModel main)
    {
        var window = await HeadlessWorld.OnUi(() =>
        {
            var created = new MainWindow(main);
            created.Width = 1400;
            created.Height = 900;
            return created;
        });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }
}
