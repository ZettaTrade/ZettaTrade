namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Livery.Localization;
using Xunit;
using ZettaTrade.App.Views;

/// <summary>
/// The headless localization fact (T-144): a language flip re-inks the
/// window's static strings live — the loc:Localized markup rides the
/// manager's language stream, so the REAL window's menu captions
/// re-compose without a reopen. The fact flips through the REAL manager
/// (the world initialized it with the shipped catalog) and restores the
/// standing language in a finally, so the process-global state survives
/// even a failed assert. Every visual-tree read rides the world's
/// user-interface lane — an off-thread Header read dies on VerifyAccess.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class HeadlessLocalizationTests
{
    static HeadlessLocalizationTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task ALanguageFlip_ReInksTheWindowsStaticStrings()
    {
        var world = await MainWorld.CreateAsync();
        var manager = LocalizationManager.Instance;
        var standing = manager.CurrentLanguage;

        var window = await HeadlessWorld.OnUi(() => new MainWindow { DataContext = world.Main });
        await HeadlessWorld.ShowAndSettleAsync(window);
        try
        {
            try
            {
                // Flip to a known pair first: the standing language depends
                // on the machine's OS culture, the flip's shape does not.
                await FlipAsync(manager, "en");
                await FileMenuHasAsync(
                    window, manager.GetString("Menu.File"), "the bar re-inks in English");

                await FlipAsync(manager, "ru");
                await FileMenuHasAsync(
                    window, manager.GetString("Menu.File"), "the flip re-inks the static caption live");
            }
            finally
            {
                // The restore runs even on a failed assert: the language is
                // process-global state the other facts must not inherit.
                await FlipAsync(manager, standing);
            }

            // The restore itself re-inks: the standing caption is back.
            await FileMenuHasAsync(
                window, manager.GetString("Menu.File"), "the restore re-inks the standing caption");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The File menu carries the expected caption, re-read on the
    /// user-interface lane until it re-inks or the budget runs out.
    /// </summary>
    private static async Task FileMenuHasAsync(Window window, string expected, string because)
    {
        var budget = TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (true)
        {
            var found = await HeadlessWorld.OnUi(() => HeadlessWorld.Descendants<MenuItem>(window)
                .Any(item => item.Header as string == expected));
            if (found)
            {
                return;
            }

            if (DateTime.UtcNow - started > budget)
            {
                throw new Xunit.Sdk.XunitException(
                    $"The static string never re-inked to '{expected}': {because}");
            }

            await Task.Delay(20);
            await AvaloniaDispatcherPump.DrainAsync();
        }
    }

    /// <summary>Flip the language on the user-interface thread and land the marshal.</summary>
    private static async Task FlipAsync(LocalizationManager manager, string language)
    {
        Dispatcher.UIThread.Post(() => _ = manager.SetLanguageAsync(language));
        await Task.Delay(50);
        await AvaloniaDispatcherPump.DrainAsync();
    }
}
