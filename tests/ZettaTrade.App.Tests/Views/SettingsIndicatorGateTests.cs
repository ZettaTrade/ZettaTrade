namespace ZettaTrade.App.Tests.Views;

using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The indicators tab's gate pins (T-067 for the SMA row, T-092 for the
/// pack): the XAML wires every overlay row's visibility to its loaded
/// plugin flag, the honest empty state to the catalog-is-empty flag, and
/// the localized hints resolve in en and ru. The app has no headless render
/// harness, so the view-side facts ride the source of truth (the same
/// precedent the window chrome pins carry).
/// </summary>
public sealed class SettingsIndicatorGateTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();
    private static readonly string SettingsViewFile = Path.Combine(
        RepoRoot,
        "src",
        "ZettaTrade.App",
        "Views",
        "SettingsWindow.axaml");

    [Fact]
    public void TheIndicatorsTab_GatesTheRowOnTheLoadedPluginFlag()
    {
        var source = File.ReadAllText(SettingsViewFile);

        source.Should().Contain(
            """
            IsVisible="{Binding IsSmaPluginLoaded}"
            """,
            "the SMA row is a view over the scanned catalog, it never assumes a shipped assembly");
        source.Should().Contain(
            """
            IsVisible="{Binding IsIndicatorCatalogEmpty}"
            """,
            "the empty state shows only for a catalog with zero registered indicators");
        source.Should().Contain(
            "{loc:Localized Settings.Indicators.Empty}",
            "the empty-state hint is a localized user-facing string");
    }

    /// <summary>
    /// The pack's rows (T-092): the six bundled plugins render under the
    /// exact same gate binding shape the SMA row carries — each row keyed
    /// by its own loaded-plugin flag.
    /// </summary>
    [Theory]
    [InlineData("IsEmaPluginLoaded", "Chart.Indicators.EmaTip")]
    [InlineData("IsWmaPluginLoaded", "Chart.Indicators.WmaTip")]
    [InlineData("IsRsiPluginLoaded", "Chart.Indicators.RsiTip")]
    [InlineData("IsMacdPluginLoaded", "Chart.Indicators.MacdTip")]
    [InlineData("IsAtrPluginLoaded", "Chart.Indicators.AtrTip")]
    [InlineData("IsBollPluginLoaded", "Chart.Indicators.BollTip")]
    public void TheIndicatorsTab_GatesEachPackRowOnItsOwnLoadedPluginFlag(string gateFlag, string tipKey)
    {
        var source = File.ReadAllText(SettingsViewFile);

        source.Should().Contain(
            $$"""
            IsVisible="{Binding {{gateFlag}}}"
            """,
            "the {0} row is a view over the scanned catalog, it never assumes a shipped assembly",
            gateFlag);
        source.Should().Contain(
            $$"""{loc:Localized {{tipKey}}}""",
            "the {0} hint is a localized user-facing string",
            tipKey);
    }

    /// <summary>
    /// The pack's tips: every row's hint keys resolve in both cultures —
    /// the same en/ru invariant the SMA's empty state pins.
    /// </summary>
    [Theory]
    [InlineData("Chart.Indicators.EmaTip")]
    [InlineData("Chart.Indicators.WmaTip")]
    [InlineData("Chart.Indicators.RsiTip")]
    [InlineData("Chart.Indicators.MacdTip")]
    [InlineData("Chart.Indicators.AtrTip")]
    [InlineData("Chart.Indicators.BollTip")]
    public void ThePackRowHints_ResolveInBothCultures(string key)
    {
        Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            .Should().NotBeNullOrEmpty("'{0}' is a user-facing gate string", key);
        Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().NotBeNullOrEmpty("'{0}' must localize into Russian", key);
    }

    [Fact]
    public void TheEmptyStateHint_ResolvesInBothCultures()
    {
        const string key = "Settings.Indicators.Empty";

        Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            .Should().NotBeNullOrEmpty("'{0}' is a user-facing gate string", key);
        Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().NotBeNullOrEmpty("'{0}' must localize into Russian", key);
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
