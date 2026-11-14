namespace ZettaTrade.App.Tests.Views.Headless;

using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.App.Views.Panels;

/// <summary>
/// The transcript's virtualization pins (T-218): the chat transcript rides
/// a <c>VirtualizingStackPanel</c> now — a several-hundred-turn
/// conversation realizes the scrolled window, never the whole stack,
/// while the scroll semantics stay exactly what the non-virtualized
/// stack delivered. The app has no auto-follow anywhere (the view model
/// never touches the scroll), so the preserved shape is: a scroll-to-end
/// chase lands on the true bottom and follows an appended turn to the
/// new max exactly once (the chase's overscroll base is spent by its own
/// landing), a turn appended at a finite bottom stays below the fold,
/// and a streamed tail growth rebases the offset under the anchoring
/// instead of yanking the reader. The facts ride the full main world —
/// the seeded turns are variable-height (the long answers wrap on the
/// assistant column's narrow viewport), the realized bounds read the
/// panel's public FirstRealizedIndex / LastRealizedIndex, and the class
/// joins the LocalizationManagerSingleton collection with the
/// process-wide dispatcher pump installed.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessChatTranscriptTests
{
    static HeadlessChatTranscriptTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The transcript's own ScrollViewer: the one whose content is the
    /// ItemsControl bound to the chat VM's ChatMessages — the picker's
    /// internal template viewers are excluded by the source check.
    /// </summary>
    private static ScrollViewer FindTranscriptViewer(AssistantPanelView panel, ChatViewModel chat) =>
        panel.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(viewer => viewer.Content is ItemsControl items &&
                ReferenceEquals(items.ItemsSource, chat.ChatMessages));

    /// <summary>
    /// The realized window's shape at one instant: the offset, the max and
    /// the panel's realized index bounds — the tuple every scroll fact
    /// reads back on the serialized dispatcher.
    /// </summary>
    private static (double Offset, double Max, int FirstRealized, int LastRealized) ReadWindow(
        AssistantPanelView panel, ChatViewModel chat)
    {
        var viewer = FindTranscriptViewer(panel, chat);
        var virtualized = (VirtualizingStackPanel)((ItemsControl)viewer.Content!).Presenter!.Panel!;
        return (viewer.Offset.Y, viewer.ScrollBarMaximum.Y, virtualized.FirstRealizedIndex, virtualized.LastRealizedIndex);
    }

    /// <summary>
    /// The topmost container that intersects the viewport, read back as the
    /// turn's content and its viewport position — the anchor the stream
    /// facts hold steady.
    /// </summary>
    private static (string Content, double Y) TopVisibleTurn(AssistantPanelView panel, ChatViewModel chat)
    {
        var viewer = FindTranscriptViewer(panel, chat);
        var containers = ((ItemsControl)viewer.Content!).Presenter!.Panel!.Children;
        var rows = containers
            .Select(container => new
            {
                Container = container,
                Y = container.TranslatePoint(new Point(0, 0), viewer)?.Y ?? double.NaN,
                Content = ((ChatMessageViewModel)container.DataContext!).Content,
            })
            .Where(row => !double.IsNaN(row.Y) &&
                row.Y < viewer.Viewport.Height &&
                row.Y + row.Container.Bounds.Height > 0)
            .OrderBy(row => row.Y)
            .ToList();
        rows.Should().NotBeEmpty("the bottom view shows realized turns to hold steady");
        return (rows[0].Content, rows[0].Y);
    }

    /// <summary>
    /// Seed a several-hundred-turn conversation with variable heights: the
    /// long turns ride the assistant column's narrow viewport into
    /// multi-line wraps, the rest stay one-line — the estimate's swing the
    /// anchoring has to ride.
    /// </summary>
    private static void SeedTranscript(ChatViewModel chat, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var text = i % 5 == 0
                ? $"turn {i}: " + string.Join(" ", Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 12))
                : $"turn {i}: short turn";
            chat.ChatMessages.Add(new ChatMessageViewModel
            {
                Kind = i % 3 == 0 ? ChatMessageKind.UserTurn : ChatMessageKind.AssistantTurn,
                Role = i % 3 == 0 ? "You" : "AI",
                Content = text,
            });
        }
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

    /// <summary>
    /// The open transcript realizes a window, not the whole stack: the
    /// items panel IS a VirtualizingStackPanel, the realized index window
    /// opens at the first turns and stays a handful of containers wide,
    /// and every realized container renders exactly one bubble.
    /// </summary>
    [Fact]
    public async Task TheLongTranscript_RealizesAWindow_NotTheWholeStack()
    {
        var world = await MainWorld.CreateAsync();
        SeedTranscript(world.Main.Chat, 300);
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var panel = HeadlessWorld.Descendants<AssistantPanelView>(window).Single();
                var viewer = FindTranscriptViewer(panel, world.Main.Chat);
                var items = (ItemsControl)viewer.Content!;
                var virtualized = items.Presenter!.Panel.Should().BeOfType<VirtualizingStackPanel>()
                    .Which;
                virtualized.FirstRealizedIndex.Should().Be(0, "the transcript opens on its first turns");
                (virtualized.LastRealizedIndex + 1).Should().BeLessThan(
                    20, "the open realizes the scrolled window, never the whole stack");
                virtualized.Children.Should().HaveCountLessThan(
                    20, "the containers realize per window, not per conversation");
                HeadlessWorld.Descendants<Border>(panel)
                    .Count(bubble => bubble.Classes.Contains("message"))
                    .Should().Be(virtualized.Children.Count,
                        "each realized container renders exactly its bubble");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The scroll-to-end chase lands on the true bottom and stays there:
    /// the offset reads the scrolled max, the realized window closes on
    /// the conversation's actual last turn, and a second chase pass moves
    /// nothing (the bottom is the bottom).
    /// </summary>
    [Fact]
    public async Task TheScrollToEnd_LandsOnTheTrueBottom_AndStaysThere()
    {
        var world = await MainWorld.CreateAsync();
        var chat = world.Main.Chat;
        SeedTranscript(chat, 300);
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            // Two chase passes: the second absorbs the estimate corrections
            // the first realize pass may trigger.
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            var first = await HeadlessWorld.OnUi(() => ReadWindow(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            var second = await HeadlessWorld.OnUi(() => ReadWindow(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));

            first.Offset.Should().Be(first.Max, "the chase lands on the scrolled max");
            first.LastRealized.Should().Be(chat.ChatMessages.Count - 1,
                "the conversation's actual last turn is realized at the bottom");
            second.Offset.Should().Be(first.Offset, "the landed bottom is stable across the settle pass");
            second.Max.Should().Be(first.Max, "the landed extent is stable across the settle pass");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// A turn appended right after a scroll-to-end follows to the new max
    /// exactly once: the chase's overscroll base is spent by its own
    /// landing, so the next appended turn stays below the fold — the
    /// app has no auto-follow to invent, and the virtualized host must
    /// not invent one either.
    /// </summary>
    [Fact]
    public async Task TheTurnAppended_AfterAScrollToEnd_FollowsTheNewMax_Once()
    {
        var world = await MainWorld.CreateAsync();
        var chat = world.Main.Chat;
        SeedTranscript(chat, 300);
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();

            chat.ChatMessages.Add(new ChatMessageViewModel
            {
                Kind = ChatMessageKind.AssistantTurn,
                Role = "AI",
                Content = "a fresh turn appended at the bottom",
            });
            await AvaloniaDispatcherPump.DrainAsync();
            var after = await HeadlessWorld.OnUi(() => ReadWindow(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));

            after.Offset.Should().Be(after.Max,
                "the spent chase base follows the appended turn to the new max");

            chat.ChatMessages.Add(new ChatMessageViewModel
            {
                Kind = ChatMessageKind.AssistantTurn,
                Role = "AI",
                Content = "a second turn while the reader holds",
            });
            await AvaloniaDispatcherPump.DrainAsync();
            var held = await HeadlessWorld.OnUi(() => ReadWindow(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));

            held.Offset.Should().Be(after.Offset,
                "the chase is one-shot: after it lands the reader holds the position");
            held.Max.Should().BeGreaterThan(after.Max, "the extent grows under the fold");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// A turn appended at a finite bottom (the wheel's endpoint — where a
    /// reader actually is) stays put: the view never auto-follows, the
    /// new turn renders under the fold and the reader decides when to
    /// walk down to it.
    /// </summary>
    [Fact]
    public async Task TheTurnAppended_AtAFiniteBottom_StaysPut()
    {
        var world = await MainWorld.CreateAsync();
        var chat = world.Main.Chat;
        SeedTranscript(chat, 300);
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();

            // Replace the chase's overscroll base with the wheel's endpoint:
            // a finite offset at the exact bottom.
            var before = await HeadlessWorld.OnUi(() =>
            {
                var panel = HeadlessWorld.Descendants<AssistantPanelView>(window).Single();
                var viewer = FindTranscriptViewer(panel, chat);
                viewer.Offset = new Vector(0, viewer.ScrollBarMaximum.Y);
                return ReadWindow(panel, chat);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            chat.ChatMessages.Add(new ChatMessageViewModel
            {
                Kind = ChatMessageKind.AssistantTurn,
                Role = "AI",
                Content = "a turn appended at a finite bottom",
            });
            await AvaloniaDispatcherPump.DrainAsync();
            var after = await HeadlessWorld.OnUi(() => ReadWindow(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));

            after.Offset.Should().Be(before.Offset, "a finite bottom holds: the view never auto-follows");
            after.Max.Should().BeGreaterThan(before.Max, "the extent grows under the fold");
            after.FirstRealized.Should().Be(before.FirstRealized,
                "the realized window stays on the same turns: the view did not jump");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// A streamed tail growth holds the reader steady: growing the last
    /// turn in place swings the height estimate and rebases the offset
    /// under the anchoring, and the visible turn keeps its exact viewport
    /// position — the stream never yanks the reader, and the realized
    /// window stays bounded while it happens.
    /// </summary>
    [Fact]
    public async Task TheStreamedTailGrowth_HoldsTheReaderSteady()
    {
        var world = await MainWorld.CreateAsync();
        var chat = world.Main.Chat;
        SeedTranscript(chat, 300);
        var window = await ShowMainWindowAsync(world.Main);
        try
        {
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
                FindTranscriptViewer(HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat)
                    .ScrollToEnd());
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
            {
                var panel = HeadlessWorld.Descendants<AssistantPanelView>(window).Single();
                var viewer = FindTranscriptViewer(panel, chat);
                viewer.Offset = new Vector(0, viewer.ScrollBarMaximum.Y);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            var anchor = await HeadlessWorld.OnUi(() => TopVisibleTurn(
                HeadlessWorld.Descendants<AssistantPanelView>(window).Single(), chat));

            // The stream flush: the tail turn grows in place (the same
            // mutation the reply loop's render flush performs on the row).
            chat.ChatMessages[^1].Content = "turn 299: " +
                string.Join(" ", Enumerable.Repeat("growing streaming answer text", 60));
            await AvaloniaDispatcherPump.DrainAsync();

            var held = await HeadlessWorld.OnUi(() =>
            {
                var panel = HeadlessWorld.Descendants<AssistantPanelView>(window).Single();
                var viewer = FindTranscriptViewer(panel, chat);
                var containers = ((ItemsControl)viewer.Content!).Presenter!.Panel!.Children;
                var same = containers
                    .Select(container => new
                    {
                        Container = container,
                        Y = container.TranslatePoint(new Point(0, 0), viewer)?.Y ?? double.NaN,
                        Content = ((ChatMessageViewModel)container.DataContext!).Content,
                    })
                    .Single(row => row.Content == anchor.Content);
                return (same.Y, containers.Count);
            });

            held.Item1.Should().BeApproximately(anchor.Y, 1.0,
                "the anchoring holds the visible turn in place while the estimate rebases");
            held.Item2.Should().BeLessThan(20,
                "the realized window stays bounded through the streamed growth");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }
}
