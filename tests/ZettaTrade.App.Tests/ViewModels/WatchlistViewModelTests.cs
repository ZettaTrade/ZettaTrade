namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The watchlist panel's facts (T-196): the snapshot diff keeps stable row
/// identities (a moved row moves once) and the selection rides the symbol
/// text (a reorder re-points the index, a retire clears it), the list
/// surface mirrors the catalog's document in place (the combo's collection
/// identity never changes), the list shim routes switches through the
/// echo-guard, the inline editor gates its confirm and routes create and
/// rename, the delete gate exempts the starter and re-arms after a rename
/// away from it, the row commands route into the catalog within the exact
/// bounds, the starter's display name re-inks with the language, and the
/// empty state flips only through the published rows.
/// </summary>
/// <remarks>
/// Runs inside <see cref="AvaloniaDispatcherSerialCollection"/>: the VM's
/// document and quote subscriptions ride <c>ObserveOnUiThread</c>, so the
/// publishes and snapshots land only through
/// <see cref="AvaloniaDispatcherPump.DrainAsync"/> — the catalog's own
/// seed publish (the real store reads synchronously) queues at the VM's
/// construction and the first drain lands it.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class WatchlistViewModelTests
{
    static WatchlistViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>The starter row order the panel claims (mirrored, not read).</summary>
    private static readonly string[] StarterTexts =
    [
        "BTC/USDT",
        "ETH/USDT",
        "SOL/USDT",
        "XRP/USDT",
        "ADA/USDT",
        "DOGE/USDT",
        "BNB/USDT",
        "LINK/USDT",
        "AVAX/USDT",
        "LTC/USDT",
    ];

    /// <summary>The empty starter roster with a populated sibling: the empty-hint facts' stored posture.</summary>
    private const string StoredEmptyActive =
        """{"version":1,"active":"Majors","lists":[{"name":"Majors","symbols":[]},{"name":"Alts","symbols":["TRX/USDT"]}]}""";

    #region Fixtures

    /// <summary>
    /// One panel over a real catalog (a real store in a fresh temp
    /// directory) and the real feed over a substituted adapter — the
    /// catalog's seed completes synchronously, so the VM's initial publish
    /// is queued at construction and the first drain lands it.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(string? stored = null)
        {
            Directory = Path.Combine(Path.GetTempPath(), "t196-vm-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Settings = new SettingsService(
                Path.Combine(Directory, "settings.json"),
                NullLogger<SettingsService>.Instance);
            if (stored is not null)
            {
                Settings.SetAsync(WatchlistSettingsKeys.Section, WatchlistSettingsKeys.DocumentKey, stored)
                    .GetAwaiter()
                    .GetResult();
            }

            Catalog = new WatchlistCatalogService(Settings, NullLogger<WatchlistCatalogService>.Instance);
            Tickers = new Dictionary<string, Subject<Ticker>>(StringComparer.Ordinal);
            Adapter = Substitute.For<IMarketDataAdapter>();
            Adapter
                .SubscribeTicker(Arg.Any<Symbol>())
                .Returns(callInfo => LaneFor(Tickers, callInfo.Arg<Symbol>().Normalized));
            Selections = new Subject<Symbol>();
            Pulse = new Subject<long>();
            Feed = new WatchlistFeedService(
                Adapter,
                Selections,
                Catalog,
                NullLogger<WatchlistFeedService>.Instance,
                Pulse);
            Localization = new FakeLocalization();
            Panel = new WatchlistViewModel(
                Feed,
                Catalog,
                Localization,
                NullLogger<WatchlistViewModel>.Instance);
        }

        public string Directory { get; }

        public SettingsService Settings { get; }

        public WatchlistCatalogService Catalog { get; }

        public IMarketDataAdapter Adapter { get; }

        public Dictionary<string, Subject<Ticker>> Tickers { get; }

        public Subject<Symbol> Selections { get; }

        public Subject<long> Pulse { get; }

        public WatchlistFeedService Feed { get; }

        public FakeLocalization Localization { get; }

        public WatchlistViewModel Panel { get; }

        /// <summary>One pulse beat plus the drain: the snapshot lands on the panel.</summary>
        public async Task PulseAsync()
        {
            Pulse.OnNext(0L);
            await AvaloniaDispatcherPump.DrainAsync();
        }

        /// <summary>The published rows as normalized symbol texts.</summary>
        public string[] RowTexts() => Panel.Rows.Select(row => row.SymbolText).ToArray();

        /// <summary>A quote snapshot of unpriced rows over the given texts, in order.</summary>
        public static IReadOnlyList<WatchlistQuote> Snapshot(params string[] symbolTexts) =>
        [
            .. symbolTexts.Select(text => new WatchlistQuote(text, null, null, null, Array.Empty<decimal>())),
        ];

        public void Dispose()
        {
            Feed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Catalog.Dispose();
            Settings.Dispose();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // The store's file watcher can hold the directory a beat
                // longer on some platforms — the temp directory dies with
                // the OS cleanup either way.
            }
        }

        /// <summary>The lane cache behind the substituted SubscribeTicker.</summary>
        private static Subject<Ticker> LaneFor(Dictionary<string, Subject<Ticker>> tickers, string normalized)
        {
            if (!tickers.TryGetValue(normalized, out var lane))
            {
                lane = new Subject<Ticker>();
                tickers.Add(normalized, lane);
            }

            return lane;
        }
    }

    /// <summary>The two-language localization fake (the palette facts' kit, keyed for the watchlist).</summary>
    private sealed class FakeLocalization : ILocalizationManager
    {
        private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
        {
            [DefaultListNameKey] = "All majors",
        };

        private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>
        {
            [DefaultListNameKey] = "Основные",
        };

        private const string DefaultListNameKey = "Watch.DefaultListName";

        private IReadOnlyDictionary<string, string> _catalog = English;

        public Subject<string> Changes { get; } = new();

        public string CurrentLanguage => _catalog == Russian ? "ru" : "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } =
        [
            new LanguageInfo("en", "English", "English", true),
            new LanguageInfo("ru", "Russian", "Русский", false),
        ];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default)
        {
            SwitchTo(cultureCode);
            return Task.CompletedTask;
        }

        /// <summary>Swap the catalog and land the change exactly like the manager's language stream does.</summary>
        public void SwitchTo(string cultureCode)
        {
            _catalog = cultureCode == "ru" ? Russian : English;
            Changes.OnNext(cultureCode);
        }

        public string GetString(string key) => _catalog.GetValueOrDefault(key, "[" + key + "]");

        public string GetString(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, GetString(key), args);

        public IObservable<string> LanguageChanged => Changes;

        public IObservable<string> GetObservable(string key) =>
            Observable.Defer(() => Observable.Return(GetString(key)))
                .Concat(Changes.Select(_ => GetString(key)));
    }

    /// <summary>
    /// One panel over a substitute store whose seeded read returns null
    /// synchronously (the starter stands): the store writes count through
    /// the <c>WhenForAnyArgs</c> trace, so the shim's echo-guard asserts
    /// exactly how many switches a write sequence really issued. The feed
    /// stays real (a sealed class substitutes for nothing) over the same
    /// substituted adapter the store fixture rides.
    /// </summary>
    private sealed class SubstituteFixture : IDisposable
    {
        public SubstituteFixture()
        {
            StoreWrites = 0;
            Settings = Substitute.For<ISettingsService>();
            Settings
                .GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(_ => Task.FromResult<string>(null!));
            Settings
                .WhenForAnyArgs(settings => settings.SetAsync<string>(null!, null!, null!))
                .Do(_ => StoreWrites++);
            Catalog = new WatchlistCatalogService(Settings, NullLogger<WatchlistCatalogService>.Instance);

            Tickers = new Dictionary<string, Subject<Ticker>>(StringComparer.Ordinal);
            Adapter = Substitute.For<IMarketDataAdapter>();
            Adapter
                .SubscribeTicker(Arg.Any<Symbol>())
                .Returns(callInfo => LaneFor(Tickers, callInfo.Arg<Symbol>().Normalized));
            Feed = new WatchlistFeedService(
                Adapter,
                new Subject<Symbol>(),
                Catalog,
                NullLogger<WatchlistFeedService>.Instance,
                new Subject<long>());

            Localization = new FakeLocalization();
            Panel = new WatchlistViewModel(
                Feed,
                Catalog,
                Localization,
                NullLogger<WatchlistViewModel>.Instance);
        }

        public ISettingsService Settings { get; }

        public int StoreWrites { get; private set; }

        public WatchlistCatalogService Catalog { get; }

        public IMarketDataAdapter Adapter { get; }

        public Dictionary<string, Subject<Ticker>> Tickers { get; }

        public WatchlistFeedService Feed { get; }

        public FakeLocalization Localization { get; }

        public WatchlistViewModel Panel { get; }

        public void Dispose()
        {
            Feed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Catalog.Dispose();
        }

        /// <summary>The lane cache behind the substituted SubscribeTicker.</summary>
        private static Subject<Ticker> LaneFor(Dictionary<string, Subject<Ticker>> tickers, string normalized)
        {
            if (!tickers.TryGetValue(normalized, out var lane))
            {
                lane = new Subject<Ticker>();
                tickers.Add(normalized, lane);
            }

            return lane;
        }
    }

    #endregion

    #region The rows

    [Fact]
    public void AReorder_MovesEachRowOnce_WithTheIdentityStable()
    {
        using var fixture = new Fixture();
        var panel = fixture.Panel;
        panel.ApplyQuotes(Fixture.Snapshot(StarterTexts));

        var moves = 0;
        var structural = 0;
        var before = panel.Rows.ToList();
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action == NotifyCollectionChangedAction.Move)
            {
                moves++;
            }
            else
            {
                structural++;
            }
        }

        panel.Rows.CollectionChanged += OnChanged;

        panel.ApplyQuotes(Fixture.Snapshot([.. StarterTexts.Reverse()]));
        panel.Rows.CollectionChanged -= OnChanged;

        moves.Should().BeGreaterThan(0, "the reversed order lands as moves, never as rebuild");
        structural.Should().Be(0, "the reversed order never touches the collection's membership");
        panel.Rows.Select(row => row.SymbolText)
            .Should().Equal([.. StarterTexts.Reverse()], "the snapshot order IS the panel's");
        panel.Rows[9].Should().BeSameAs(before[0], "the rows keep their identities through the reorder");
        panel.Rows[0].Should().BeSameAs(before[9]);
        panel.RowCount.Should().Be(StarterTexts.Length);
        panel.HasRows.Should().BeTrue();
    }

    [Fact]
    public void TheSelectionFixUp_RePointsThroughAReorder_AndClearsOnARetire()
    {
        using var fixture = new Fixture();
        var panel = fixture.Panel;

        panel.ApplyQuotes(Fixture.Snapshot("BTC/USDT", "ETH/USDT"));
        panel.SelectedRowIndex = 0;
        panel.SelectedRowIndex.Should().Be(0);

        panel.ApplyQuotes(Fixture.Snapshot("ETH/USDT", "BTC/USDT"));
        panel.SelectedRowIndex.Should().Be(1, "the selection rides the symbol text, not the index");

        panel.ApplyQuotes(Fixture.Snapshot("ETH/USDT"));
        panel.SelectedRowIndex.Should().Be(-1, "the feed retired the selection's row: the index clears");
    }

    [Fact]
    public async Task TheEmptyHint_FlipsOnlyThroughThePublishedRows()
    {
        using var fixture = new Fixture(StoredEmptyActive);
        await AvaloniaDispatcherPump.DrainAsync();

        fixture.Panel.HasRows.Should().BeFalse("the active list carries no symbols");
        fixture.Panel.RowCount.Should().Be(0);

        await fixture.Catalog.AddSymbolAsync("Majors", "BTC/USDT");
        await fixture.PulseAsync();

        fixture.Panel.HasRows.Should().BeTrue("the mutation's publish re-armed the row set");
        fixture.Panel.RowCount.Should().Be(1);
        fixture.RowTexts().Should().Equal(["BTC/USDT"]);
    }

    #endregion

    #region The list surface

    [Fact]
    public async Task TheListSurface_MirrorsTheDocument_InPlace()
    {
        using var fixture = new Fixture();
        await AvaloniaDispatcherPump.DrainAsync();

        var panel = fixture.Panel;
        var starterOption = panel.Lists.Should().ContainSingle().Which;
        starterOption.Name.Should().Be(WatchlistSettingsKeys.StarterListName);
        starterOption.DisplayName.Should().Be("All majors", "the starter token shows the localized default");
        starterOption.IsStarter.Should().BeTrue();
        panel.SelectedListIndex.Should().Be(0);

        await fixture.Catalog.CreateListAsync("Alts");
        await AvaloniaDispatcherPump.DrainAsync();

        panel.Lists.Should().HaveCount(2);
        panel.Lists[0].Should().BeSameAs(starterOption, "the in-place diff keeps equal options untouched");
        panel.Lists[1].Name.Should().Be("Alts");
        panel.Lists[1].DisplayName.Should().Be("Alts", "a custom list shows its own stored name");
        panel.Lists[1].IsStarter.Should().BeFalse();
        panel.SelectedListIndex.Should().Be(1, "the create set the new list active");

        await fixture.Catalog.DeleteListAsync("Alts");
        await AvaloniaDispatcherPump.DrainAsync();

        panel.Lists.Should().ContainSingle("the delete restored the starter roster");
        panel.SelectedListIndex.Should().Be(0);
    }

    [Fact]
    public async Task TheListShim_SwitchesThroughTheEchoGuard_AndTheResetNeverApplies()
    {
        using var fixture = new SubstituteFixture();
        await AvaloniaDispatcherPump.DrainAsync();

        await fixture.Catalog.CreateListAsync("Alts");
        await AvaloniaDispatcherPump.DrainAsync();
        fixture.Panel.SelectedListIndex.Should().Be(1, "the create set the new list active");

        fixture.Panel.SelectedListIndex = 0;
        await AvaloniaDispatcherPump.DrainAsync();

        fixture.StoreWrites.Should().Be(2, "the create's write plus the one switch the shim issued");
        fixture.Catalog.Current.ActiveList.Should().Be("Majors");
        fixture.Panel.SelectedListIndex.Should().Be(0, "the publish re-pointed the shim");

        fixture.Panel.SelectedListIndex = 0;
        fixture.StoreWrites.Should().Be(2, "the echo write short-circuits before the guard even reads");

        fixture.Panel.SelectedListIndex = -1;
        fixture.StoreWrites.Should().Be(2, "the control's transient reset applies nothing");
    }

    [Fact]
    public async Task ThePublish_NeverClosesTheEditor()
    {
        using var fixture = new SubstituteFixture();
        var panel = fixture.Panel;

        await panel.OpenNewListCommand.Execute(RxVoid.Default);
        panel.IsListEditorOpen.Should().BeTrue();

        _ = fixture.Catalog.AddSymbolAsync("Majors", "TRX/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        panel.IsListEditorOpen.Should().BeTrue("a publish re-points the surface, the editor stays");
    }

    #endregion

    #region The list editor

    [Fact]
    public async Task TheEditor_GatesItsConfirm_AndRoutesCreateAndRename()
    {
        using var fixture = new SubstituteFixture();
        var panel = fixture.Panel;
        await AvaloniaDispatcherPump.DrainAsync();

        await panel.OpenNewListCommand.Execute(RxVoid.Default);
        panel.IsListEditorOpen.Should().BeTrue();
        panel.IsListEditorRename.Should().BeFalse();
        panel.ListEditorText.Should().BeEmpty();
        (await GatedAsync(panel.ConfirmListEditorCommand)).Should()
            .BeFalse("a blank name cannot apply");

        panel.ListEditorText = "majors";
        (await GatedAsync(panel.ConfirmListEditorCommand)).Should()
            .BeFalse("names stay unique case-insensitively");

        panel.ListEditorText = "DeFi";
        (await GatedAsync(panel.ConfirmListEditorCommand)).Should().BeTrue();

        await panel.ConfirmListEditorCommand.Execute(RxVoid.Default);
        panel.IsListEditorOpen.Should().BeFalse("the confirm closes the editor");
        await AvaloniaDispatcherPump.DrainAsync();
        fixture.Catalog.Current.Lists.Select(list => list.Name).Should().Equal(["Majors", "DeFi"]);
        fixture.Catalog.Current.ActiveList.Should().Be("DeFi", "the create sets the new list active");

        await panel.OpenRenameListCommand.Execute(RxVoid.Default);
        panel.IsListEditorOpen.Should().BeTrue();
        panel.IsListEditorRename.Should().BeTrue();
        panel.ListEditorText.Should().Be("DeFi", "the editor opens over the active list's name");

        panel.ListEditorText = "Majors";
        (await GatedAsync(panel.ConfirmListEditorCommand)).Should()
            .BeFalse("the rename target is the only name allowed to repeat");

        panel.ListEditorText = "Perps";
        await panel.ConfirmListEditorCommand.Execute(RxVoid.Default);

        fixture.Catalog.Current.Lists.Select(list => list.Name).Should().Equal(["Majors", "Perps"]);
        fixture.Catalog.Current.ActiveList.Should().Be("Perps", "the rename re-points the active name");
    }

    [Fact]
    public async Task TheCancel_DiscardsTheEditorWithoutApplying()
    {
        using var fixture = new SubstituteFixture();
        var panel = fixture.Panel;

        await panel.OpenNewListCommand.Execute(RxVoid.Default);
        panel.ListEditorText = "DeFi";
        await panel.CancelListEditorCommand.Execute(RxVoid.Default);

        panel.IsListEditorOpen.Should().BeFalse();
        panel.ListEditorText.Should().BeEmpty();
        fixture.Catalog.Current.Lists.Should().ContainSingle("the cancel wrote nothing");
    }

    [Fact]
    public async Task TheDeleteGate_ExemptsTheStarter_AndReArmsAfterARenameAway()
    {
        using var fixture = new SubstituteFixture();
        var panel = fixture.Panel;
        await AvaloniaDispatcherPump.DrainAsync();

        panel.CanDeleteList.Should().BeFalse("the starter list is the document's anchor");
        (await GatedAsync(panel.DeleteListCommand)).Should().BeFalse();

        await fixture.Catalog.CreateListAsync("Alts");
        await AvaloniaDispatcherPump.DrainAsync();
        panel.CanDeleteList.Should().BeTrue("the active list is a custom one now");
        (await GatedAsync(panel.DeleteListCommand)).Should().BeTrue();

        await fixture.Catalog.SetActiveAsync("Majors");
        await AvaloniaDispatcherPump.DrainAsync();
        panel.CanDeleteList.Should().BeFalse("the starter is active again");

        await fixture.Catalog.RenameListAsync("Majors", "Base");
        await AvaloniaDispatcherPump.DrainAsync();
        panel.SelectedListIndex.Should().Be(0, "the rename kept the active list's index");
        panel.CanDeleteList.Should().BeTrue("the active list is no longer the starter — re-armed");
        panel.Lists[0].IsStarter.Should().BeFalse("the anchor's identity followed the rename");

        await panel.DeleteListCommand.Execute(RxVoid.Default);

        fixture.Catalog.Current.Lists.Select(list => list.Name).Should().Equal(["Alts"]);
        fixture.Catalog.Current.ActiveList.Should().Be("Alts", "the delete fell back to the remaining list");

        await AvaloniaDispatcherPump.DrainAsync();
        await panel.DeleteListCommand.Execute(RxVoid.Default);

        fixture.Catalog.Current.Should().BeEquivalentTo(
            WatchlistCodec.StarterDocument(),
            "the last list's delete restored the starter state in full");
    }

    #endregion

    #region The row commands

    [Fact]
    public async Task TheRowCommands_RouteIntoTheCatalog_WithinExactBounds()
    {
        using var fixture = new Fixture();
        var panel = fixture.Panel;
        await AvaloniaDispatcherPump.DrainAsync();

        panel.ApplyQuotes(Fixture.Snapshot(StarterTexts));
        panel.SelectedRowIndex = 0;
        (await GatedAsync(panel.MoveRowUpCommand)).Should().BeFalse("the head row cannot move up");
        (await GatedAsync(panel.MoveRowDownCommand)).Should().BeTrue();

        await panel.MoveRowDownCommand.Execute(RxVoid.Default);
        fixture.Catalog.Current.Lists[0].Symbols.Take(2).Should().Equal(["ETH/USDT", "BTC/USDT"]);

        // The pulse lands the reordered rows; the selection re-points to
        // the moved row's identity.
        await fixture.PulseAsync();
        panel.SelectedRowIndex.Should().Be(1, "the selection re-pointed through the reorder");

        (await GatedAsync(panel.MoveRowUpCommand)).Should().BeTrue();
        await panel.MoveRowUpCommand.Execute(RxVoid.Default);
        fixture.Catalog.Current.Lists[0].Symbols.Take(2).Should().Equal(["BTC/USDT", "ETH/USDT"]);
        await fixture.PulseAsync();
        panel.SelectedRowIndex.Should().Be(0);

        panel.SelectedRowIndex = StarterTexts.Length - 1;
        (await GatedAsync(panel.MoveRowDownCommand)).Should()
            .BeFalse("the tail row cannot move down");
        (await GatedAsync(panel.RemoveRowCommand)).Should().BeTrue();

        await panel.RemoveRowCommand.Execute(RxVoid.Default);
        fixture.Catalog.Current.Lists[0].Symbols.Should().NotContain("LTC/USDT");
        fixture.Catalog.Current.Lists[0].Symbols.Should().HaveCount(StarterTexts.Length - 1);

        await fixture.PulseAsync();
        panel.SelectedRowIndex.Should().Be(-1, "the removed row's selection cleared");
        (await GatedAsync(panel.RemoveRowCommand)).Should().BeFalse("no row stands selected");
    }

    [Fact]
    public async Task ApplyPickedAsync_AddsToTheActiveList_AndDuplicatesStayNoOps()
    {
        using var fixture = new Fixture();
        var panel = fixture.Panel;
        await AvaloniaDispatcherPump.DrainAsync();

        await panel.ApplyPickedAsync(Symbol.Parse("TRX/USDT"));

        fixture.Catalog.Current.Lists[0].Symbols.Should().Contain("TRX/USDT");
        fixture.Catalog.Current.Lists[0].Symbols.Should().HaveCount(StarterTexts.Length + 1);

        await panel.ApplyPickedAsync(Symbol.Parse("TRX/USDT"));

        fixture.Catalog.Current.Lists[0].Symbols.Should().HaveCount(
            StarterTexts.Length + 1,
            "the duplicate pick never appends twice");
    }

    #endregion

    #region The starter display name

    [Fact]
    public async Task TheStarterDisplayName_ReInksWithTheLanguage()
    {
        using var fixture = new Fixture();
        await AvaloniaDispatcherPump.DrainAsync();

        fixture.Panel.Lists[0].DisplayName.Should().Be("All majors");

        fixture.Localization.SwitchTo("ru");
        await AvaloniaDispatcherPump.DrainAsync();

        fixture.Panel.Lists[0].DisplayName.Should().Be("Основные", "the language switch re-inked the token");
        fixture.Panel.Lists[0].Name.Should().Be(WatchlistSettingsKeys.StarterListName, "the stored name never changes");
    }

    #endregion

    /// <summary>
    /// Read a ReactiveCommand's gate: the <see cref="ReactiveCommand{RxVoid, RxVoid}.CanExecute"/>
    /// stream's current value, sampled fresh at the fact's call.
    /// </summary>
    private static async Task<bool> GatedAsync(ReactiveCommand<RxVoid, RxVoid> command) =>
        await command.CanExecute.FirstAsync();
}
