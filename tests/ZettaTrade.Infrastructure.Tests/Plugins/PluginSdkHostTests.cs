namespace ZettaTrade.Infrastructure.Tests.Plugins;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Plugins;
using ZettaTrade.Plugins.Fixtures;
using ZettaTrade.Plugins.Fixtures.Hazards;

/// <summary>
/// The SDK loader matrix (T-131): the valid sample and the dependency
/// chain award their loads and publish the registry, the hazard shapes —
/// duplicate ids, missing dependencies, unparsable versions,
/// manifest-less entries, cycles, garbage dlls, partly unreadable
/// assemblies — refuse loudly without harming the survivors, a missing
/// plugins directory means nothing but an empty report, and the DI shape
/// wires the whole family through one registration.
/// </summary>
public sealed class PluginSdkHostTests
{
    #region Lifecycle

    public PluginSdkHostTests()
    {
        // The fixtures assembly persists statics across this class's
        // facts: the recorder resets at every instance.
        ChainStartOrder.Started.Clear();
        SamplePanelPlugin.InitializeCalls = 0;
        SamplePanelPlugin.LastContext = null;
    }

    #endregion

    #region Facts

    [Fact]
    public async Task SampleAndChainLoadAndPublishTheirExtensions()
    {
        using var stage = new PluginSdkStage();
        stage.StageFixtures();

        var (host, provider, registry) = await stage.RunAsync();

        host.Report.LoadedPluginIds.Should().Equal((IEnumerable<string>)["fx.alpha", "fx.beta", "fx.sample"],
            "the valid fixtures all load.");
        host.Report.RefusedPluginIds.Should().BeEmpty("no hazard was staged.");
        host.Report.Errors.Should().BeEmpty("no hazard was staged.");

        registry.Snapshot.Panels.Should()
            .ContainSingle("the sample registered one panel")
            .Which.PluginId.Should().Be("fx.sample");
        registry.Snapshot.Panels[0].Panel.PanelId.Should().Be("panel-a");
        registry.Snapshot.Panels[0].Panel.TitleResxKey.Should().Be("Fx.Sample.PanelTitle");

        registry.Snapshot.ToolbarItems.Should()
            .ContainSingle("the sample registered one toolbar item");
        registry.Snapshot.ToolbarItems[0].Item.ItemId.Should().Be("tool-a");
        registry.Snapshot.ToolbarItems[0].Item.LabelResxKey.Should().Be("Fx.Sample.ToolTitle");

        registry.Snapshot.SettingsSections.Should()
            .ContainSingle("the sample registered one settings section");
        registry.Snapshot.SettingsSections[0].Section.SectionId.Should().Be("section-a");

        SamplePanelPlugin.LastContext.Should().NotBeNull("the loader ran the one-time start.");
        SamplePanelPlugin.LastContext!.PluginId.Should().Be("fx.sample");
        SamplePanelPlugin.InitializeCalls.Should().Be(1, "the start is one-time.");
        ChainStartOrder.Started.Should()
            .Equal((IEnumerable<string>)["fx.beta", "fx.alpha"],
                "the dependency rule: the declared dependency start first.");

        using (provider)
        {
            Assert.True(true, "the provider disposes with the fact.");
        }
    }

    [Fact]
    public async Task HazardShapesRefuseLoudlyAndSpareTheSurvivors()
    {
        using var stage = new PluginSdkStage();
        stage.StageFixtures();
        stage.StageHazards();

        var (host, provider, registry) = await stage.RunAsync();

        host.Report.LoadedPluginIds.Should().Equal((IEnumerable<string>)["fx.alpha", "fx.beta"],
            "the duplicate id refuses the sample while the chain members sail through.");

        host.Report.RefusedPluginIds.Should().Equal((IEnumerable<string>)[
            typeof(ManifestLessPlugin).FullName!,
            "fx.bad-version",
            "fx.ghost-dep",
            "fx.ring-a",
            "fx.ring-b",
            "fx.sample",
            "fx.self"], "the refusal list stays sorted (ordinal caps lead), ids where an identity existed.");

        var errors = string.Join(' ', host.Report.Errors);
        errors.Should().Contain("claimed by 2 types", "the duplicate id is loud.");
        errors.Should().Contain("'fx.does-not-exist'", "the missing dependency is loud.");
        errors.Should().Contain("not SemVer 2", "the bad version is loud.");
        errors.Should().Contain("without the ZettaTradePlugin manifest", "the manifest-less entry is loud.");
        host.Report.Errors.Count(e => e.Contains("dependency cycle")).Should().Be(3,
            "the self cycle, both ring members refuse.");

        ChainStartOrder.Started.Should().Equal((IEnumerable<string>)["fx.beta", "fx.alpha"],
            "the survivors keep the dependency order.");

        using (provider)
        {
            Assert.True(true, "the provider disposes with the fact.");
        }
    }

    [Fact]
    public async Task GarbageAssemblyWarnsAndTheScanContinues()
    {
        using var stage = new PluginSdkStage();
        stage.StageGarbage();
        stage.StageFixtures();

        var (host, provider, registry) = await stage.RunAsync();

        host.Report.Errors.Should().BeEmpty("a garbage dll is a warning, not a refusal.");
        host.Report.LoadedPluginIds.Should().Equal((IEnumerable<string>)["fx.alpha", "fx.beta", "fx.sample"],
            "the scan continues past the garbage file.");
        stage.Loggers.Faults.Should().Contain(f => f is BadImageFormatException,
            "the garbage file's read fault reached the log.");

        using (provider)
        {
            Assert.True(true, "the provider disposes with the fact.");
        }
    }

    [Fact]
    public async Task PartlyUnreadableAssemblyDoesNotBreakTheScan()
    {
        // The indicator-family Broken fixture: a valid dll whose base class
        // lives in an assembly deliberately never staged, so its GetTypes
        // partially fails — the SDK scan tolerates it per the T-054 canon
        // and serves whatever readable types exist (none here are SDK
        // plugins, so the world stays empty and healthy).
        var source = System.IO.Path.Combine(AppContext.BaseDirectory, "ZettaTrade.Plugins.Broken.dll");
        Assert.True(File.Exists(source), "the broken plugin dll ships in the test output.");

        using var stage = new PluginSdkStage();
        File.Copy(source, System.IO.Path.Combine(stage.Path, "ZettaTrade.Plugins.Broken.dll"), overwrite: true);

        var (host, provider, registry) = await stage.RunAsync();

        host.Report.Errors.Should().BeEmpty("the partial read is a tolerance, not a refusal.");
        host.Report.RefusedPluginIds.Should().BeEmpty();
        registry.Snapshot.Panels.Should().BeEmpty("the broken dll carries no SDK plugin.");

        using (provider)
        {
            Assert.True(true, "the provider disposes with the fact.");
        }
    }

    [Fact]
    public async Task MissingDirectoryMeansAnEmptyWorld()
    {
        using var stage = new PluginSdkStage();
        Directory.Delete(stage.Path);

        var (host, provider, registry) = await stage.RunAsync();

        host.Report.LoadedPluginIds.Should().BeEmpty();
        host.Report.RefusedPluginIds.Should().BeEmpty();
        host.Report.Errors.Should().BeEmpty("a missing directory is not a fault.");
        registry.Snapshot.Panels.Should().BeEmpty();

        using (provider)
        {
            Assert.True(true, "the provider disposes with the fact.");
        }
    }

    [Fact]
    public void TheRegistryAnswersEmptyBeforeTheWalk()
    {
        new PluginExtensionRegistry().Snapshot.Panels.Should().BeEmpty();
        new PluginExtensionRegistry().Snapshot.ToolbarItems.Should().BeEmpty();
        new PluginExtensionRegistry().Snapshot.SettingsSections.Should().BeEmpty();
    }

    [Fact]
    public void TheDiShapeWiresLoaderAndRegistryTogether()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddPluginSdk("/a/scan/directory/the/tests/never/walk");
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<ILogger<PluginSdkHost>>(NullLogger<PluginSdkHost>.Instance);

        var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<PluginSdkHost>();
        var registry = provider.GetRequiredService<IPluginExtensionRegistry>();

        host.Report.LoadedPluginIds.Should().BeEmpty("the walk has not run yet.");
        host.Report.Errors.Should().BeEmpty();
        registry.Snapshot.Panels.Should().BeEmpty("the walk has not run yet.");
        Assert.True(ReferenceEquals(host.Extensions, registry),
            "the host writes the registry the shell reads.");
    }

    #endregion
}
