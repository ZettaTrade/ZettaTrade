namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The hotkey manager's test kit (T-102): a real service/ViewModel pair
/// over substitute settings — the stored document answers the seed read,
/// the changes stream is an open subject the tests push into, every
/// <c>SetAsync</c> write lands in a capture list — and a localizer that
/// echoes keys back (multi-argument lookups echo as <c>[key:args]</c>),
/// so names and verdicts read as visible assertions instead of hidden
/// plumbing. Every substituted task completes synchronously, so the
/// service's construction-time seed and the persist leg land inline; the
/// only scheduled hop is the ViewModel's <c>ObserveOnUiThread</c> map
/// mirror, which the pump-based facts drain explicitly.
/// </summary>
/// <param name="Service">The live-map service.</param>
/// <param name="Settings">The settings substitute (the document's store).</param>
/// <param name="Changes">The open changes stream (push the stored documents here).</param>
/// <param name="SavedDocuments">Every document the service persisted, in write order.</param>
/// <param name="Localization">The echoing localizer.</param>
/// <param name="LanguageChanged">The open language switch stream.</param>
internal sealed record HotkeyKit(
    HotkeyService Service,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    List<string> SavedDocuments,
    ILocalizationManager Localization,
    Subject<string> LanguageChanged)
{
    /// <summary>Gets the manager ViewModel the menu and the composition share.</summary>
    public required HotkeyViewModel ViewModel { get; init; }

    /// <summary>Push one stored document through the changes stream (the settings round trip's write half).</summary>
    public void ApplyDocument(string document) =>
        Changes.OnNext(new SettingsChange(
            UiSettingsKeys.HotkeySection,
            UiSettingsKeys.HotkeyOverrides,
            UiSettingsKeys.DefaultHotkeyOverrides,
            document));
}

/// <summary>The hotkey kit's builders.</summary>
internal static class HotkeyKits
{
    /// <summary>
    /// A fully wired kit: the stored document answers the seed read (null
    /// keeps the shipped empty map), the streams are open subjects, the
    /// writes land in the capture list.
    /// </summary>
    /// <param name="storedDocument">The stored overrides document the seed read answers.</param>
    internal static HotkeyKit CreateKit(string? storedDocument = null)
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>())
            .Returns(storedDocument ?? UiSettingsKeys.DefaultHotkeyOverrides);
        var saved = new List<string>();
        settings
            .When(s => s.SetAsync(UiSettingsKeys.HotkeySection, UiSettingsKeys.HotkeyOverrides, Arg.Any<string>()))
            .Do(call => saved.Add(call.ArgAt<string>(2)));

        var localization = Substitute.For<ILocalizationManager>();
        var languageChanged = new Subject<string>();
        localization.LanguageChanged.Returns(languageChanged);
        localization
            .GetString(Arg.Any<string>())
            .Returns(call => call.ArgAt<string>(0));
        localization
            .GetString(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");

        var service = new HotkeyService(
            settings,
            NullLogger<HotkeyService>.Instance);
        var viewModel = new HotkeyViewModel(
            service,
            localization,
            NullLogger<HotkeyViewModel>.Instance);

        return new HotkeyKit(
            service,
            settings,
            changes,
            saved,
            localization,
            languageChanged)
        {
            ViewModel = viewModel,
        };
    }

    /// <summary>An anonymous manager off the kit (the menu tests' collaborator).</summary>
    internal static HotkeyViewModel CreateAnonymousHotkeys() => CreateKit().ViewModel;
}
