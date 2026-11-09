namespace ZettaTrade.Infrastructure.Tests.SingleInstance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The dispatch lane's pins (T-104): the OS open decides the claim (one
/// holder, a disposed claim is immediately re-acquirable), the gate maps
/// every second start to its own prompt exit code — the grammar refusal
/// before any pipe traffic, a forward that finds its listener (golden:
/// the same raw argv lands on the receiver and the receipt returns), a
/// forward into a dead pipe after a bounded window — and the owned start
/// carries the claim for the process's whole lifetime. Every fact's waits
/// are delivery-ordered or bounded by the injected schedule; no wall-clock
/// budgets anywhere.
/// </summary>
public sealed class SingleInstanceDispatchTests
{
    /// <summary>The empty schedule: exactly one immediate connect attempt, zero waits.</summary>
    private static readonly HandoffForwardSchedule Prompt = new([], ImmediateConnectTimeoutMs);

    /// <summary>The one-attempt connect bound keeps the dead-pipe fact fast.</summary>
    private const int ImmediateConnectTimeoutMs = 300;

    #region The claim

    [Fact]
    public void TheClaimFile_AcquiresExactlyOnce_AndReacquiresAfterRelease()
    {
        var lockFilePath = TestLockPath();

        try
        {
            var claim = SingleInstanceClaim.TryAcquire(lockFilePath);

            claim.Should().NotBeNull("a fresh lock file belongs to the first claimer");

            // Cross-instance exclusivity: a second acquire (the same test
            // process stands for the second app instance here) is refused.
            // The fact is mortal on platforms whose per-process lock tables
            // collapse same-process double claims; the shipping contract
            // only requires the cross-PROCESS exclusivity, which the OS
            // guarantees on all three.
            SingleInstanceClaim.TryAcquire(lockFilePath).Should().BeNull(
                "a held claim is owned by its holder until release");

            claim!.Dispose();

            SingleInstanceClaim.TryAcquire(lockFilePath).Should().NotBeNull(
                "a released claim is free for the next start at once");
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(lockFilePath) ?? throw new InvalidOperationException("no lock directory"));
        }
    }

    #endregion The claim

    #region The gate and the golden

    [Fact]
    public async Task TheOwnedStart_WhenTheClaimIsFree_IsTheOwnedOutcome()
    {
        var scene = await ClaimScene.CreateAsync();

        try
        {
            var outcome = SingleInstanceGate.Enter(["--symbol", "BTC/USDT"], scene.LockFilePath, scene.PipeName, Prompt);

            outcome.Should().BeOfType<LaunchOutcome.Owned>(
                "the free claim file belongs to the first start, whatever its arguments carry");
        }
        finally
        {
            await scene.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheGrammarRefusal_LeavesThePipeUntouched()
    {
        var scene = await ClaimScene.CreateAsync(ownedElsewhere: true);

        try
        {
            var outcome = SingleInstanceGate.Enter(["--order", "1"], scene.LockFilePath, scene.PipeName, Prompt);
            var redirected = outcome.Should().BeOfType<LaunchOutcome.Redirected>().Subject;

            redirected.ExitCode.Should().Be(LaunchExitCode.ArgsRejected);
            redirected.Detail.Should().Contain("grammar");
            scene.AppliedAsync.IsCompleted.Should().BeFalse("a refused vector never forwards, so the receiver never runs");
        }
        finally
        {
            await scene.DisposeAsync();
        }
    }

    [Fact]
    public void TheDeadPipeForward_SettlesPromptlyUnavailable()
    {
        // No host anywhere: the one immediate attempt reads the silence and
        // the outcome is the bounded unavailability, never a hang.
        var lockFilePath = TestLockPath();
        try
        {
            var held = SingleInstanceClaim.TryAcquire(lockFilePath) ?? throw new InvalidOperationException("test precondition");
            try
            {
                var outcome = SingleInstanceGate.Enter(
                    ["--symbol", "BTC/USDT"],
                    lockFilePath,
                    "zettatrade-handoff-test-absent-" + Guid.NewGuid().ToString("N"),
                    Prompt);

                outcome.Should().BeEquivalentTo(new LaunchOutcome.Redirected(
                    LaunchExitCode.HandoffUnavailable,
                    "the handoff pipe never served a forward within the bounded window"));
            }
            finally
            {
                held.Dispose();
            }
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(lockFilePath) ?? throw new InvalidOperationException("no lock directory"));
        }
    }

    [Fact]
    public async Task TheGoldenForward_DeliversTheSameVector_ReturnsTheReceipt_Word()
    {
        var scene = await ClaimScene.CreateAsync(ownedElsewhere: true);
        scene.StartHost();

        try
        {
            var outcome = SingleInstanceGate.Enter(
                ["--symbol", "BTC/USDT", "--exchange", "demo"],
                scene.LockFilePath,
                scene.PipeName,
                Prompt);
            var redirected = outcome.Should().BeOfType<LaunchOutcome.Redirected>().Subject;

            redirected.ExitCode.Should().Be(LaunchExitCode.Forwarded);
            redirected.Detail.Should().Be("settled");
            (await scene.AppliedAsync).Should()
                .Equal((IEnumerable<string>)["--symbol", "BTC/USDT", "--exchange", "demo"],
                    "the receiver re-validates the exact raw vector, so the forward never rewrites it");
        }
        finally
        {
            await scene.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheEmptyVectorForward_IsTheCleanNoSelectionRedirect()
    {
        var scene = await ClaimScene.CreateAsync(ownedElsewhere: true);
        scene.StartHost();

        try
        {
            var outcome = SingleInstanceGate.Enter([], scene.LockFilePath, scene.PipeName, Prompt);
            var redirected = outcome.Should().BeOfType<LaunchOutcome.Redirected>().Subject;

            redirected.ExitCode.Should().Be(
                LaunchExitCode.Forwarded,
                "a plain second start (no arguments at all) redirects cleanly and applies nothing");
            // The receipt settled BEFORE the client's return, and a
            // no-selection vector short-circuits BEFORE the apply route
            // (the receipt IS the no-selection word): the apply runner can
            // never run afterwards, deterministically.
            scene.AppliedAsync.IsCompleted.Should().BeFalse(
                "the no-selection receipt short-circuits the apply route - the receiver applied nothing");
        }
        finally
        {
            await scene.DisposeAsync();
        }
    }

    #endregion The gate and the golden

    #region The scene kit

    private static string TestLockPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "zettatrade-single-instance-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "single-instance.lock");
    }

    private static void TryCleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The test directory may still carry a momentarily-open lock
            // handle on the platform's runtime; a leftover temp directory
            // of a finished fact is not a defect the suite owns.
        }
    }

    /// <summary>
    /// The dispatch test's scene: a real claim file, a marker claim
    /// (optionally) standing for the running instance, and a live host on
    /// the test's own pipe with a recording apply runner. Everything the
    /// scene claims is closed in the same breath the fact ends.
    /// </summary>
    private sealed class ClaimScene : IAsyncDisposable
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _appliedDelivery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private IDisposable? _markerClaim;

        private SingleInstanceHost? _host;

        private CancellationTokenSource? _hostStop;

        public string LockFilePath { get; }

        public string PipeName { get; }

        /// <summary>The apply delivery promise (already subscribed; settles at the receiver's apply).</summary>
        public Task<IReadOnlyList<string>> AppliedAsync => _appliedDelivery.Task;

        private ClaimScene(string lockFilePath, string pipeName) => (LockFilePath, PipeName) = (lockFilePath, pipeName);

        /// <summary>
        /// Create the scene with the marker claim either held (a start that
        /// redirects) or absent (a start that owns). The marker claim and
        /// the directory live until the scene's own dispose.
        /// </summary>
        public static async Task<ClaimScene> CreateAsync(bool ownedElsewhere = false)
        {
            var lockFilePath = TestLockPath();
            var pipeName = "zettatrade-handoff-test-" + Guid.NewGuid().ToString("N");
            var scene = new ClaimScene(lockFilePath, pipeName);
            if (ownedElsewhere)
            {
                scene._markerClaim = SingleInstanceClaim.TryAcquire(lockFilePath)
                    ?? throw new InvalidOperationException("the marker claim could not be taken");
            }

            await Task.CompletedTask;
            return scene;
        }

        /// <summary>Open the host's pipe and keep the scene's stop token for the fact's close.</summary>
        public void StartHost()
        {
            _hostStop = new CancellationTokenSource();
            _host = new SingleInstanceHost(
                PipeName,
                ApplyRunnerAsync,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SingleInstanceHost>.Instance);
            _host.Start();
        }

        /// <summary>
        /// The recording runner: the raw vector lands in the delivery
        /// promise at the receiver's apply, then the settled receipt
        /// returns — the client's own return already proved both legs.
        /// </summary>
        private Task<HandoffReceipt> ApplyRunnerAsync(
            IReadOnlyList<string> args,
            CancellationToken ct)
        {
            _appliedDelivery.TrySetResult(args);
            return Task.FromResult(new HandoffReceipt(true, "settled"));
        }

        public async ValueTask DisposeAsync()
        {
            // The pump stops first, so the host's serial server cannot
            // accept again while the scene's claim closes.
            _hostStop?.Cancel();
            _markerClaim?.Dispose();
            try
            {
                Directory.Delete(
                    Path.GetDirectoryName(LockFilePath) ?? throw new InvalidOperationException("no lock directory"),
                    recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory of a finished scene is not a
                // defect the suite owns.
            }

            await Task.CompletedTask;
        }
    }

    #endregion The scene kit
}
