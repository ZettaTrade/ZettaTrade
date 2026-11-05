namespace ZettaTrade.App.Tests.Views;

using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The indicators tab's gate pins (T-067): the XAML wires the overlay row's
/// visibility to the loaded-plugin flag, the honest empty state to the
/// catalog-is-empty flag, and the localized hint resolves in en and ru.
/// The app has no headless render harness, so the view-side facts ride the
/// source of truth (the same precedent the window chrome pins carry).
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
