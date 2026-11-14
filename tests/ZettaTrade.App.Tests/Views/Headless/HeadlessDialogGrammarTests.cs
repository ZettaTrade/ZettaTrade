namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Views;

/// <summary>
/// The headless dialog-grammar facts (T-231): the REAL dialog windows
/// materialize headlessly and the grammar is MEASURED, not just
/// source-pinned — the fixed-frame era's end is the window's own
/// CanResize answer, the paired confirm strips measure quiet-first and
/// the ONE primary last inside the client area at the 90 strip, and the
/// annotation prompt materializes under the styled chrome. The windows
/// materialize WITHOUT a view model: the activation bodies are null-safe
/// (the scroll-gutter lane's precedent), the strips are the views' own
/// layout, and every window touch rides the world's user-interface lane
/// (an off-thread constructor dies on VerifyAccess). The facts share the
/// LocalizationManager singleton, so the class rides the
/// LocalizationManagerSingleton collection.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessDialogGrammarTests
{
    static HeadlessDialogGrammarTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The paired confirm strips and the primary class their LAST button
    /// carries. The confirmation gate names none: the strip unifies the
    /// shape while the buttons stay equal-weight (the T-183 discipline).
    /// </summary>
    private static readonly (Type Window, string PrimaryClass)[] PairedStrips =
    [
        (typeof(AmendOrderDialogWindow), "primary"),
        (typeof(TradeNoteDialogWindow), "primary"),
        (typeof(PaperResetDialogWindow), "primary"),
        (typeof(ProtectiveLevelsDialogWindow), "primary"),
        (typeof(LicenseActivationWindow), "primary"),
        (typeof(NewOrderDialogWindow), "cta"),
        (typeof(ConfirmationDialogWindow), ""),
    ];

    /// <summary>
    /// The dialog family the task unified — every window that shipped the
    /// fixed frame before T-231 materializes with the resizable frame on
    /// the real object, not just in the source.
    /// </summary>
    [Fact]
    public async Task TheDialogFamily_MaterializesResizable()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        foreach (var windowType in new[]
                 {
                     typeof(AmendOrderDialogWindow),
                     typeof(ConfirmationDialogWindow),
                     typeof(CrashDialogWindow),
                     typeof(DisclaimerDialogWindow),
                     typeof(FirstRunWizardWindow),
                     typeof(KillSwitchReportWindow),
                     typeof(LicenseActivationWindow),
                     typeof(NewOrderDialogWindow),
                     typeof(PaperResetDialogWindow),
                     typeof(ProtectiveLevelsDialogWindow),
                     typeof(TextAnnotationWindow),
                     typeof(TradeNoteDialogWindow),
                 })
        {
            var window = await HeadlessWorld.OnUi(() =>
                (Window)Activator.CreateInstance(windowType)!);
            await HeadlessWorld.ShowAndSettleAsync(window);
            try
            {
                await HeadlessWorld.OnUi(() =>
                    window.CanResize.Should().BeTrue(
                        "the {0} resizes: the fixed-frame era ended app-wide", windowType.Name));
            }
            finally
            {
                await HeadlessWorld.CloseAndSettleAsync(window);
            }
        }
    }

    /// <summary>
    /// The paired confirm strips measure the settings footer's shape on
    /// the real layout: exactly one right-aligned row per window, every
    /// strip button riding the 90 width, the quiet button first, the ONE
    /// primary last, and the pair sitting side by side INSIDE the client
    /// area (quiet.Left &lt; primary.Right at the shipped width).
    /// </summary>
    [Fact]
    public async Task ThePairedConfirmStrips_MeasureQuietFirstPrimaryLast()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        foreach (var (windowType, primaryClass) in PairedStrips)
        {
            var window = await HeadlessWorld.OnUi(() =>
                (Window)Activator.CreateInstance(windowType)!);
            await HeadlessWorld.ShowAndSettleAsync(window);
            try
            {
                await HeadlessWorld.OnUi(() =>
                {
                    var strip = HeadlessWorld.Descendants<StackPanel>(window)
                        .Single(panel =>
                            panel.Orientation == Orientation.Horizontal
                            && panel.HorizontalAlignment == HorizontalAlignment.Right
                            && panel.Children.OfType<Button>().Count() >= 2);
                    var buttons = strip.Children.OfType<Button>().ToList();

                    foreach (var button in buttons)
                    {
                        button.MinWidth.Should().Be(
                            90,
                            "the {0}'s strip rides the settings footer's 90 step", windowType.Name);
                    }

                    if (primaryClass.Length == 0)
                    {
                        buttons[^1].Classes.Should().NotContain(
                            "primary",
                            "the confirmation gate stays equal-weight (the T-183 discipline)");
                        buttons[0].Classes.Should().NotContain("cta");
                    }
                    else
                    {
                        buttons[0].Classes.Should().NotContain(
                            primaryClass,
                            "the {0}'s quiet button leads the strip", windowType.Name);
                        buttons[^1].Classes.Should().Contain(
                            primaryClass,
                            "the {0}'s ONE primary action closes the strip", windowType.Name);
                    }

                    // The measured side-by-side: the quiet's left edge sits
                    // before the last button's right edge, and the row
                    // stays inside the client area at the shipped width.
                    var firstLeft = buttons[0].TranslatePoint(new Point(0, 0), window)!.Value.X;
                    var lastRight = buttons[^1].TranslatePoint(
                        new Point(buttons[^1].Bounds.Width, 0), window)!.Value.X;
                    firstLeft.Should().BeLessThan(
                        lastRight,
                        "the {0}'s strip reads left-to-right: quiet before the primary", windowType.Name);
                    lastRight.Should().BeLessThanOrEqualTo(
                        window.ClientSize.Width,
                        "the {0}'s strip stays inside the client area (nothing clips at the shipped width)", windowType.Name);
                });
            }
            finally
            {
                await HeadlessWorld.CloseAndSettleAsync(window);
            }
        }
    }

    /// <summary>
    /// The annotation prompt materializes under the styled chrome: the
    /// compact chrome class rides the real window's Classes (the
    /// WindowDecorations="Full" era ended with it).
    /// </summary>
    [Fact]
    public async Task TheAnnotationPrompt_MaterializesUnderTheStyledChrome()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        var window = await HeadlessWorld.OnUi(() => new TextAnnotationWindow());
        await HeadlessWorld.ShowAndSettleAsync(window);
        try
        {
            await HeadlessWorld.OnUi(() =>
                window.Classes.Should().Contain(
                    "chrome-compact",
                    "the annotation prompt rides the compact chrome every dialog speaks"));
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }
}
