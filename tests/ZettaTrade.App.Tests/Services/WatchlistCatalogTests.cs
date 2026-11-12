namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The watchlist catalog's facts (T-196) over a real store in a fresh
/// per-test temp directory (and one gate-controlled substitute for the
/// seed's own ordering): a fresh install stands on the starter, a stored
/// document applies normalized (duplicates drop, a dangling active name
/// falls back, an emptied roster restores the starter, an unreadable or
/// foreign-version payload keeps the starter), every mutation's
/// accept/refuse/no-op shape holds, the starter list is the deletion
/// anchor, and the mutations wait for the stored read before they write.
/// </summary>
public sealed class WatchlistCatalogTests
{
    private const string Payload = """{"version":1,"active":"Majors","lists":[{"name":"Majors","symbols":["BTC/USDT","ETH/USDT"]},{"name":"Alts","symbols":[]}]}""";

    #region Fixtures

    /// <summary>One catalog over a real store in a fresh temp directory (an optional stored payload seeds it first).</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(string? stored = null)
        {
            Directory = Path.Combine(Path.GetTempPath(), "t196-catalog-" + Guid.NewGuid().ToString("N"));
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
            Published = [];
            Catalog.Changes.Subscribe(Published.Add);
        }

        public string Directory { get; }

        public SettingsService Settings { get; }

        public WatchlistCatalogService Catalog { get; }

        /// <summary>The publishes the test subscription caught (the seed itself lands inside the catalog's construction).</summary>
        public List<WatchlistDocument> Published { get; }

        public void Dispose()
        {
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
    }

    /// <summary>One catalog over a substitute whose seeded read waits on a gate: the seed's own publish is observable.</summary>
    private sealed class GatedFixture : IDisposable
    {
        public GatedFixture()
        {
            Gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            FirstPublish = new TaskCompletionSource<WatchlistDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            Settings = Substitute.For<ISettingsService>();
            Settings
                .GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(_ => Gate.Task);
            StoreWrites = 0;
            Settings
                .When(settings => settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
                .Do(_ => StoreWrites++);
            Catalog = new WatchlistCatalogService(Settings, NullLogger<WatchlistCatalogService>.Instance);
            Published = [];
            Catalog.Changes.Subscribe(Published.Add);
            Catalog.Changes.Subscribe(publish => FirstPublish.TrySetResult(publish));
        }

        public TaskCompletionSource<string> Gate { get; }

        /// <summary>The delivery-ordered promise of the first publish (the T-135 lesson — never a wall-clock wait).</summary>
        public TaskCompletionSource<WatchlistDocument> FirstPublish { get; }

        public ISettingsService Settings { get; }

        public WatchlistCatalogService Catalog { get; }

        public List<WatchlistDocument> Published { get; }

        public int StoreWrites { get; private set; }

        public void Dispose() => Catalog.Dispose();
    }

    #endregion

    #region Seed Facts

    [Fact]
    public void AFreshInstall_StandsOnTheStarterDocument()
    {
        using var fixture = new Fixture();

        var starter = fixture.Catalog.Current;
        starter.ActiveList.Should().Be(WatchlistSettingsKeys.StarterListName);
        starter.Lists.Should().HaveCount(1);
        starter.Lists[0].Name.Should().Be(WatchlistSettingsKeys.StarterListName);
        starter.Lists[0].Symbols.Should().Equal(WatchlistCodec.StarterSymbols);
    }

    [Fact]
    public void AStoredDocument_AppliesOverTheStarter()
    {
        using var fixture = new Fixture(Payload);

        var current = fixture.Catalog.Current;
        current.ActiveList.Should().Be("Majors");
        current.Lists.Select(list => list.Name).Should().Equal(["Majors", "Alts"]);
        current.Lists[0].Symbols.Should().Equal(["BTC/USDT", "ETH/USDT"]);
    }

    [Fact]
    public void AnUnreadableStoredDocument_KeepsTheStarterStanding()
    {
        using var fixture = new Fixture("not a watchlist payload");

        fixture.Catalog.Current.Should().BeEquivalentTo(WatchlistCodec.StarterDocument());
    }

    [Fact]
    public void AForeignVersionDocument_KeepsTheStarterStanding()
    {
        using var fixture = new Fixture("""{"version":2,"active":"Majors","lists":[]}""");

        fixture.Catalog.Current.ActiveList.Should().Be(WatchlistSettingsKeys.StarterListName);
    }

    [Fact]
    public void AnEmptiedRosterDocument_RestoresTheStarter()
    {
        using var fixture = new Fixture("""{"version":1,"active":"Ghost","lists":[]}""");

        fixture.Catalog.Current.Should().BeEquivalentTo(WatchlistCodec.StarterDocument());
    }

    [Fact]
    public void ADanglingActiveName_FallsBackToTheStarterList()
    {
        using var fixture = new Fixture(
            """{"version":1,"active":"Ghost","lists":[{"name":"Majors","symbols":[]},{"name":"Alts","symbols":[]}]}""");

        fixture.Catalog.Current.ActiveList.Should().Be("Majors");
    }

    [Fact]
    public void ADanglingActiveName_FallsBackToTheFirstList_WhenTheStarterIsAbsent()
    {
        using var fixture = new Fixture(
            """{"version":1,"active":"Ghost","lists":[{"name":"Custom","symbols":["TRX/USDT"]}]}""");

        fixture.Catalog.Current.ActiveList.Should().Be("Custom");
    }

    [Fact]
    public void ADuplicateStoredName_KeepsTheFirstEntry()
    {
        using var fixture = new Fixture(
            """{"version":1,"active":"Majors","lists":[{"name":"Majors","symbols":["BTC/USDT"]},{"name":"majors","symbols":["ETH/USDT"]}]}""");

        var current = fixture.Catalog.Current;
        current.Lists.Should().HaveCount(1);
        current.Lists[0].Symbols.Should().Equal(["BTC/USDT"], "the first stored entry wins the name");
    }

    #endregion

    #region Mutation Facts

    [Fact]
    public async Task AnAddedSymbol_AppendsAndPublishes()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.AddSymbolAsync("Alts", "TRX/USDT");

        var current = fixture.Catalog.Current;
        current.Lists[1].Symbols.Should().Equal(["TRX/USDT"]);
        fixture.Published.Should().ContainSingle("one applied mutation, one publish");
        var stored = await fixture.Settings.GetAsync<string>(WatchlistSettingsKeys.Section, WatchlistSettingsKeys.DocumentKey);
        stored.Should().Contain("TRX/USDT", "the write landed before the publish");
    }

    [Fact]
    public async Task ADuplicateAdd_IsANoOpWithoutAWriteOrAPublish()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.AddSymbolAsync("Majors", "BTC/USDT");

        fixture.Published.Should().BeEmpty("the list already carries the symbol");
        fixture.Catalog.Current.Lists[0].Symbols.Should().Equal(["BTC/USDT", "ETH/USDT"]);
    }

    [Fact]
    public async Task AnAddOntoAnUnknownList_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.AddSymbolAsync("Ghost", "TRX/USDT");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ABlankSymbolText_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.AddSymbolAsync("Alts", "   ");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ARemovedSymbol_DropsTheEntry()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RemoveSymbolAsync("Majors", "BTC/USDT");

        fixture.Catalog.Current.Lists[0].Symbols.Should().Equal(["ETH/USDT"]);
        fixture.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task ARemoveOfAnUnknownSymbol_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RemoveSymbolAsync("Majors", "TRX/USDT");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task AMove_ReordersTheSymbols()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.MoveSymbolAsync("Majors", "BTC/USDT", 1);

        fixture.Catalog.Current.Lists[0].Symbols.Should().Equal(["ETH/USDT", "BTC/USDT"]);
    }

    [Fact]
    public async Task AMoveOutOfBounds_IsANoOp()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.MoveSymbolAsync("Majors", "BTC/USDT", -1);
        await fixture.Catalog.MoveSymbolAsync("Majors", "ETH/USDT", 1);

        fixture.Published.Should().BeEmpty("both moves would leave the roster");
        fixture.Catalog.Current.Lists[0].Symbols.Should().Equal(["BTC/USDT", "ETH/USDT"]);
    }

    [Fact]
    public async Task ACreate_AppendsAnEmptyList_AndSetsItActive()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.CreateListAsync("DeFi");

        var current = fixture.Catalog.Current;
        current.Lists.Select(list => list.Name).Should().Equal(["Majors", "Alts", "DeFi"]);
        current.Lists[2].Symbols.Should().BeEmpty();
        current.ActiveList.Should().Be("DeFi");
    }

    [Fact]
    public async Task ADuplicateCreate_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.CreateListAsync("alts");

        fixture.Published.Should().BeEmpty("names stay unique case-insensitively");
    }

    [Fact]
    public async Task ABlankCreate_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.CreateListAsync("   ");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ARename_RenamesAndRePointsTheActiveName()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RenameListAsync("Alts", "DeFi");

        var current = fixture.Catalog.Current;
        current.Lists.Select(list => list.Name).Should().Equal(["Majors", "DeFi"]);
    }

    [Fact]
    public async Task ARenameOfTheActiveList_KeepsTheActiveNameOnIt()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.SetActiveAsync("Alts");
        await fixture.Catalog.RenameListAsync("Alts", "DeFi");

        var current = fixture.Catalog.Current;
        current.ActiveList.Should().Be("DeFi");
    }

    [Fact]
    public async Task ADuplicateRename_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RenameListAsync("Alts", "Majors");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ARenameOntoItsOwnName_IsANoOp()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RenameListAsync("Alts", "Alts");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ARenameOfAnUnknownList_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.RenameListAsync("Ghost", "DeFi");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ADeleteOfTheStarter_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.DeleteListAsync("Majors");

        fixture.Published.Should().BeEmpty("the starter list is the document's anchor");
        fixture.Catalog.Current.Lists.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADeleteOfTheActiveList_FallsBackToTheStarter()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.SetActiveAsync("Alts");
        await fixture.Catalog.DeleteListAsync("Alts");

        var current = fixture.Catalog.Current;
        current.Lists.Select(list => list.Name).Should().Equal(["Majors"]);
        current.ActiveList.Should().Be("Majors");
    }

    [Fact]
    public async Task ADeleteOfTheLastRemainingList_RestoresTheStarterInFull()
    {
        using var fixture = new Fixture(
            """{"version":1,"active":"Custom","lists":[{"name":"Custom","symbols":["TRX/USDT"]}]}""");

        await fixture.Catalog.DeleteListAsync("Custom");

        fixture.Catalog.Current.Should().BeEquivalentTo(WatchlistCodec.StarterDocument());
    }

    [Fact]
    public async Task ADeleteOfAnUnknownList_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.DeleteListAsync("Ghost");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ASetActive_SwitchesTheActiveList()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.SetActiveAsync("Alts");

        fixture.Catalog.Current.ActiveList.Should().Be("Alts");
        fixture.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task ASetActiveOntoTheActiveList_IsANoOp()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.SetActiveAsync("majors");

        fixture.Published.Should().BeEmpty("the echo never re-writes the key");
    }

    [Fact]
    public async Task ASetActiveOntoAnUnknownList_IsRefused()
    {
        using var fixture = new Fixture(Payload);

        await fixture.Catalog.SetActiveAsync("Ghost");

        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task AMutation_WritesTheNormalizedShape_OverTheStoredPayload()
    {
        using var fixture = new Fixture(
            """{"version":1,"active":"majors","lists":[{"name":"Majors","symbols":[]},{"name":"majors","symbols":[]}]}""");

        await fixture.Catalog.AddSymbolAsync("Majors", "TRX/USDT");

        fixture.Catalog.Current.ActiveList.Should().Be("Majors", "the active re-points to the exact stored spelling");
        fixture.Catalog.Current.Lists.Should().ContainSingle("the duplicate dropped at load");
        fixture.Catalog.Current.Lists[0].Symbols.Should().Equal(["TRX/USDT"]);
    }

    #endregion

    #region Seed Ordering Facts

    [Fact]
    public async Task TheSeededRead_PublishesIntoTheChangesStream()
    {
        using var fixture = new GatedFixture();

        fixture.Gate.SetResult(Payload);
        await fixture.FirstPublish.Task;

        fixture.Published.Should().ContainSingle("the seed's own publish lands with the applied document");
        fixture.Published[0].ActiveList.Should().Be("Majors");
    }

    [Fact]
    public async Task AMutation_WaitsForTheStoredRead_BeforeItWrites()
    {
        using var fixture = new GatedFixture();

        var mutation = fixture.Catalog.CreateListAsync("Later");
        mutation.IsCompleted.Should().BeFalse("the gate holds the mutation until the stored read lands");

        fixture.Gate.SetResult(null!);
        await fixture.FirstPublish.Task;
        await mutation;

        fixture.Published.Should().HaveCount(2, "the seed's publish and the mutation's publish, in that order");
        fixture.Published[0].Should().BeEquivalentTo(WatchlistCodec.StarterDocument());
        fixture.Published[1].ActiveList.Should().Be("Later");
        fixture.StoreWrites.Should().Be(1, "the write fires only after the gate opens");
    }

    #endregion
}
