namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The tray close policy's settings battery (T-214): the seed read settles
/// inline (the stored mode, the shipped default when nothing or garbage is
/// stored — never a guessed mode), the change stream keeps the mode current
/// with the same honest parse, and the exit flag forces every close
/// through regardless of the mode.
/// </summary>
public sealed class TrayClosePolicyTests
{
    [Fact]
    public async Task NothingStored_KeepsTheShippedExitMode()
    {
        var (policy, _) = await CreateAsyncAsync(stored: string.Empty);

        policy.Mode.Should().Be(TrayCloseMode.Exit);
        policy.ShouldAllowClose().Should().BeTrue(
            "the shipped semantics: a close IS the exit until the user asks for the tray mode");
    }

    [Fact]
    public async Task AStoredTrayMode_AppliesOnTheSeedRead()
    {
        var (policy, _) = await CreateAsyncAsync(stored: TrayCloseMode.MinimizeToTray.ToString());

        policy.Mode.Should().Be(TrayCloseMode.MinimizeToTray);
        policy.ShouldAllowClose().Should().BeFalse(
            "the tray mode turns a plain close into a hide - the interception must stand ready");
    }

    [Fact]
    public async Task AGarbageStoredValue_KeepsTheDefault()
    {
        var (policy, _) = await CreateAsyncAsync(stored: "Bogus");

        policy.Mode.Should().Be(TrayCloseMode.Exit,
            "a UI preference never substitutes a guessed mode - the default stands");
        policy.ShouldAllowClose().Should().BeTrue();
    }

    [Fact]
    public async Task ALiveFlip_ReappliesTheMode()
    {
        var (policy, changes) = await CreateAsyncAsync(stored: string.Empty);

        changes.OnNext(new SettingsChange(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            null,
            TrayCloseMode.MinimizeToTray.ToString()));

        policy.Mode.Should().Be(TrayCloseMode.MinimizeToTray);
        policy.ShouldAllowClose().Should().BeFalse();

        changes.OnNext(new SettingsChange(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            TrayCloseMode.MinimizeToTray.ToString(),
            TrayCloseMode.Exit.ToString()));

        policy.Mode.Should().Be(TrayCloseMode.Exit);
        policy.ShouldAllowClose().Should().BeTrue();
    }

    [Fact]
    public async Task AGarbageLivePayload_KeepsTheStandingMode()
    {
        var (policy, changes) = await CreateAsyncAsync(stored: TrayCloseMode.MinimizeToTray.ToString());

        changes.OnNext(new SettingsChange(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            null,
            "Wrong"));

        policy.Mode.Should().Be(TrayCloseMode.MinimizeToTray,
            "an unparsable payload never poisons the standing mode");
        policy.ShouldAllowClose().Should().BeFalse();
    }

    [Fact]
    public async Task TheExitFlagForcesTheCloseThrough()
    {
        var (policy, _) = await CreateAsyncAsync(stored: TrayCloseMode.MinimizeToTray.ToString());

        policy.ShouldAllowClose().Should().BeFalse();

        policy.ArmExit();

        policy.ShouldAllowClose().Should().BeTrue(
            "an armed exit - the tray's, the menu's or the OS session end - always closes");
        policy.Mode.Should().Be(TrayCloseMode.MinimizeToTray,
            "the flag is one-way and the mode stands: the next explicit exit rides it");
    }

    [Fact]
    public void TheExitMode_AllowsTheCloseWithoutAnyArm()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(new Subject<SettingsChange>());
        var policy = new TrayClosePolicy(settings, NullLogger<TrayClosePolicy>.Instance);

        policy.ShouldAllowClose().Should().BeTrue(
            "the off state changes nothing - the shipped close semantics before any initialization");
    }

    private static async Task<(TrayClosePolicy Policy, Subject<SettingsChange> Changes)> CreateAsyncAsync(
        string stored)
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(
                AppSettingsKeys.Section,
                AppSettingsKeys.TrayCloseBehavior,
                Arg.Any<string>())
            .Returns(stored);

        var policy = new TrayClosePolicy(settings, NullLogger<TrayClosePolicy>.Instance);
        await policy.InitializeAsync();

        return (policy, changes);
    }
}
