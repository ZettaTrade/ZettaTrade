namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The launch goldens for the first-run wizard's once-flag (T-099): a fresh
/// config announces the wizard, ANY exit (finish, skip, close — all landing
/// as one mark) never re-announces on the next launch, a reset re-announces,
/// and the mark write is exactly one change per launch. Every reader is a
/// separate service instance over the same file — exactly how a restarted
/// app reads it.
/// </summary>
public sealed class FirstRunWizardStateGoldenTests
{
    private static SettingsService CreateService(string path) =>
        new(path, NullLogger<SettingsService>.Instance);

    private static Task<bool> ShouldShowAsync(SettingsService service) =>
        FirstRunWizardState.ShouldBeShownAsync(service);

    private static string NewTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "t099-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task FreshConfig_ShowsTheWizard_MarkShut_NeverNeverAgain_ResetReShows()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");

            // First launch on a fresh config: nothing announced yet — the
            // wizard shows, and ANY exit marks the announcement.
            using (var firstLaunch = CreateService(path))
            {
                await firstLaunch.InitializeAsync();

                (await ShouldShowAsync(firstLaunch))
                    .Should().BeTrue("a fresh config announced no wizard yet");

                await FirstRunWizardState.MarkShownAsync(firstLaunch);
            }

            // Second launch: the flag reloads from the file (a fresh service
            // instance, like a restarted app) and the wizard never re-shows.
            using (var secondLaunch = CreateService(path))
            {
                await secondLaunch.InitializeAsync();

                (await ShouldShowAsync(secondLaunch))
                    .Should().BeFalse("a marked announcement never re-shows the wizard");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheMark_IsExactlyOneChangePerLaunch_AndIdempotentToTheDecision()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            using var service = CreateService(path);
            await service.InitializeAsync();

            var announcements = new List<SettingsChange>();
            service.Changes
                .Where(change => change.Section == UiSettingsKeys.Section &&
                                 change.Key == UiSettingsKeys.FirstRunWizardShown)
                .Subscribe(
                    announcements.Add,
                    _ => { });

            await FirstRunWizardState.MarkShownAsync(service);
            await FirstRunWizardState.MarkShownAsync(service);

            announcements.Should().HaveCount(2, "each mark writes exactly once (a double mark is two writes)");

            (await ShouldShowAsync(service))
                .Should().BeFalse("the decision follows the flag, not the write count");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnExternallyResetFlag_ReAnnouncesOnTheNextCheck()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            using var service = CreateService(path);
            await service.InitializeAsync();

            await FirstRunWizardState.MarkShownAsync(service);
            (await ShouldShowAsync(service)).Should().BeFalse();

            // The external hand resets the flag to the first-run state (the
            // same shape the disclaimer's reset test pins).
            await service.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.FirstRunWizardShown,
                UiSettingsKeys.DefaultFirstRunWizardShown);

            (await ShouldShowAsync(service))
                .Should().BeTrue("a reset flag re-announces the wizard");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
