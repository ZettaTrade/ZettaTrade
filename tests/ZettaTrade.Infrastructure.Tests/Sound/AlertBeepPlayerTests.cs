namespace ZettaTrade.Infrastructure.Tests.Sound;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Sound;

/// <summary>
/// The beep player's T-220 facts: Play() dispatches the tone off the
/// caller's path and returns before the playback completes (the alert
/// engine's fire path never waits for a tone), a failed playback degrades
/// through the player's own warning lane — a logged warning, never a throw
/// out of Play(), never an unobserved fault — and a burst keeps every
/// dispatched playback (no most-recent-only slot). The injected
/// dispatch/attempt seam keeps every fact deterministic: no real tone, no
/// race, no wall-clock wait.
/// </summary>
public sealed class AlertBeepPlayerTests
{
    [Fact]
    public async Task Play_DispatchesTheTone_AndReturnsBeforeThePlaybackCompletes()
    {
        Action? capturedWork = null;
        var playback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var player = new AlertBeepPlayer(
            NullLogger<AlertBeepPlayer>.Instance,
            work =>
            {
                capturedWork = work;
                return playback.Task;
            },
            () => attempts++);

        player.Play();

        capturedWork.Should().NotBeNull("the dispatch handed the playback its work");
        playback.Task.IsCompleted.Should().BeFalse("the tone's playback is still in flight after Play returned");
        attempts.Should().Be(0, "the caller never ran the attempt — the dispatch owns it");

        // The rig runs the dispatched playback to its end and awaits it (the determinism the seam owes).
        capturedWork!();
        attempts.Should().Be(1);
        playback.SetResult();
        await playback.Task;
    }

    [Fact]
    public async Task APlaybackFault_LogsThroughThePlayersOwnLane_NeverThrowsOutOfPlay()
    {
        var logger = new SpyLogger();
        Task? dispatched = null;
        var fault = new InvalidOperationException("The platform tone refused to sound (test fixture).");
        var player = new AlertBeepPlayer(
            logger,
            work => dispatched = Task.Run(work),
            () => throw fault);

        var play = () => player.Play();
        play.Should().NotThrow("a failed playback is the player's degrade lane's business, not the caller's exception");

        dispatched.Should().NotBeNull();
        await dispatched!; // the body degrades the fault: the dispatch completes cleanly, the lane logs

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("degraded away", StringComparison.Ordinal) &&
            entry.Exception == fault);
    }

    [Fact]
    public void TwoPlays_TwoDispatches_NoSharedSingleSlotPlayback()
    {
        var logger = new SpyLogger();
        var attempts = 0;
        var player = new AlertBeepPlayer(
            logger,
            work =>
            {
                work();
                return Task.CompletedTask;
            },
            () => attempts++);

        player.Play();
        player.Play();

        attempts.Should().Be(2, "a burst keeps every dispatched playback (no most-recent-only slot)");
        logger.Entries.Should().BeEmpty("the happy path stays quiet");
    }

    /// <summary>The logger spy: records the level + message + exception triples (the player's own degrade lane's probe).</summary>
    private sealed class SpyLogger : ILogger<AlertBeepPlayer>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
