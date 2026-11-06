namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The first-run/second-run golden over a REAL settings file: a fresh config
/// requires the dialog, an acknowledged one never re-shows it on the next
/// launch, and a reset flag re-shows it. Every reader is a separate service
/// instance over the same file — exactly how a restarted app reads it.
/// </summary>
public sealed class DisclaimerAcknowledgmentGoldenTests
{
    private static SettingsService CreateService(string path) =>
        new(path, NullLogger<SettingsService>.Instance);

    /// <summary>
    /// The decision criterion behind the dialog: pending while the persisted
    /// version does not cover the shipped wording.
    /// </summary>
    private static async Task<bool> IsDialogPendingAsync(SettingsService service) =>
        !DisclaimerAcknowledgment.CoversCurrent(
            await DisclaimerAcknowledgment.ReadAcknowledgedVersionAsync(service));

    [Fact]
    public async Task FreshConfig_ShowsTheDialog_AcknowledgeNeverAgain_ResetReShows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t119-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");

            // First launch on a fresh config: nothing acknowledged yet —
            // the dialog shows, the acknowledge persists the shipped version.
            using (var firstLaunch = CreateService(path))
            {
                await firstLaunch.InitializeAsync();

                (await IsDialogPendingAsync(firstLaunch))
                    .Should().BeTrue("a fresh config acknowledged no disclaimer wording");

                await DisclaimerAcknowledgment.AcknowledgeAsync(firstLaunch);
            }

            // Second launch: the flag reloads from the file (a fresh service
            // instance, like a restarted app) and the dialog never re-shows.
            using (var secondLaunch = CreateService(path))
            {
                await secondLaunch.InitializeAsync();

                (await IsDialogPendingAsync(secondLaunch))
                    .Should().BeFalse("an acknowledged wording never re-shows the dialog");

                // An external hand resets the flag back to the first-run state.
                await secondLaunch.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.DisclaimerVersion,
                    UiSettingsKeys.DefaultDisclaimerVersion);
            }

            // Third launch after the reset: the dialog is pending again.
            using (var thirdLaunch = CreateService(path))
            {
                await thirdLaunch.InitializeAsync();

                (await IsDialogPendingAsync(thirdLaunch))
                    .Should().BeTrue("a reset flag re-shows the disclaimer dialog");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
