namespace ZettaTrade.App.Tests.Views;

using System;
using System.Globalization;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The symbol palette's route pins (T-095): the views have no headless render
/// harness, so the wiring rides the source-of-truth files, exactly like the
/// chrome pins. The window carries Ctrl+K bound to the palette's toggle
/// command, the overlay is a sibling host bound to
/// <c>SymbolPaletteViewModel.IsOpen</c> above the workspace, and the
/// code-behind carries the only key route: focus in and focus out through the
/// palette's interactions, the row-key route inside the palette subtree only,
/// and the scrim press as the mouse's way out.
/// </summary>
public sealed class SymbolPaletteRouteTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();
    private static readonly string ViewsDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views");

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(ViewsDirectory, "MainWindow.axaml"));

    private static string MainWindowCodeBehind =>
        File.ReadAllText(Path.Combine(ViewsDirectory, "MainWindow.axaml.cs"));

    [Fact]
    public void TheWindow_CarriesTheCtrlKBindingToThePalette()
    {
        var source = MainWindowXaml;

        source.Should().Contain(
            @"<KeyBinding Gesture=""Ctrl+K"" Command=""{Binding Palette.ToggleCommand}"" />",
            "Ctrl+K is the palette's toggle route on the window");
        // The gesture audit comment explains what the barrel binding is for:
        // every window key binding stays documented on its own terms.
        source.Should().Contain(
            "Ctrl+K        -> Palette.ToggleCommand",
            "the gesture audit keeps the palette route documented");
    }

    [Fact]
    public void TheOverlayHost_IsASiblingBoundToThePaletteOpenFlag()
    {
        var source = MainWindowXaml;

        source.Should().Contain(
            @"<Grid x:Name=""PaletteOverlay""",
            "the palette overlay is a named host");
        source.Should().Contain(
            "IsVisible=\"{Binding Palette.IsOpen}\"",
            "the whole host rides the ViewModel's open flag");
        source.Should().Contain(
            "Text=\"{Binding Palette.Query}\"",
            "the query box is the palette's own two-way property");
        source.Should().Contain(
            "ItemsSource=\"{Binding Palette.Rows}\"",
            "the rows list is the ranked page");
        source.Should().Contain(
            "SelectedIndex=\"{Binding Palette.SelectedIndex, Mode=TwoWay}\"",
            "the list's selection feeds the keyboard selection back");
        source.Should().Contain(
            "PointerPressed=\"OnPaletteScrimPressed\"",
            "the backdrop press is the mouse's way out");
        // The honest rows states ride the same overlay.
        source.Should().Contain("Palette.IsSourceWaiting", "the loading state is honest");
        source.Should().Contain("Palette.HasNoInstruments", "the empty source state is honest");
        source.Should().Contain("Palette.HasNoMatches", "the no-matches state is honest");
    }

    [Fact]
    public void TheCodeBehind_InstallsThePaletteRouteForTheWindowLifetime()
    {
        var source = MainWindowCodeBehind;

        source.Should().Contain(
            "Palette.FocusQueryRequested.RegisterHandler(FocusPaletteQueryAsync)",
            "the focus-in ask lands where the palette's box lives");
        source.Should().Contain(
            "Palette.FocusReturnRequested.RegisterHandler(FocusReturnAsync)",
            "the focus-out returns the pre-open element");
        // The handledEventsToo route is the point: the text box marks the
        // row keys handled before the bubble reach, and the palette's
        // navigation must outrank caret movement.
        source.Should().Contain(
            "PaletteOverlay.AddHandler(",
            "the route is added to the overlay subtree only");
        source.Should().Contain(
            "handledEventsToo: true",
            "the caret handling of the query box must not eat the row keys");
        source.Should().Contain(
            "PaletteOverlay.RemoveHandler(InputElement.KeyDownEvent, OnPaletteKeyDown)",
            "the route leaves with the window");
    }

    [Fact]
    public void TheKeyRoute_CarriesExactlyTheRowKeysAndTheWaysOut()
    {
        var source = MainWindowCodeBehind;

        foreach (var key in new[]
                 {
                     "case Key.Up when palette.SelectedIndex > 0:",
                     "case Key.Down when palette.HasRowAfter(palette.SelectedIndex):",
                     "case Key.PageUp when palette.SelectedIndex > 0:",
                     "case Key.PageDown when palette.HasRowAfter(palette.SelectedIndex):",
                     "case Key.Enter when palette.HasRowAt(palette.SelectedIndex):",
                     "case Key.Escape:",
                 })
        {
            source.Should().Contain(key, "the palette's key route owns {0}", key);
        }

        source.Should().Contain(
            "DispatchPaletteCommand(palette.ApplySelectionCommand)",
            "Enter's apply rides the command, gated, never a bare task");
        source.Should().Contain(
            "DispatchPaletteCommand(palette.CloseCommand)",
            "Esc and the scrim close ride the same command");
        // The route answers only while the palette stands open.
        source.Should().Contain(
            "!palette.IsOpen",
            "the workspace keys must never reach the palette route");
    }

    [Fact]
    public void TheOverlayStrings_ShipForBothCultures()
    {
        foreach (var key in new[]
                 {
                     "Palette.Title",
                     "Palette.Placeholder",
                     "Palette.Loading",
                     "Palette.Empty",
                     "Palette.NoMatches",
                     "Palette.Hint",
                 })
        {
            Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
                .Should().NotBeNullOrEmpty("'{0}' is a user-facing palette string", key);
            Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
                .Should().NotBeNullOrEmpty("'{0}' must localize into Russian", key);
        }

        // The keys write the module prefix the resx gate teaches.
        var enCatalog = File.ReadAllText(
            Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));
        enCatalog.Should().Contain("Palette.Title", "the palette keys live under the Palette. prefix");
        enCatalog.Should().Contain("Palette.Hint", "the keyboard route is discoverable from the overlay");
    }

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker (the same walk the chrome tests ride).
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
