namespace ZettaTrade.App.Tests;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

/// <summary>
/// The audit-proof Avalonia dispatcher world for App.Tests:
/// <see cref="EnsureInstalled"/> (called once, idempotent — first from the
/// xUnit test-framework hook <see cref="DispatcherFirstTestFramework"/>
/// before any discovery reflection, then from the static constructors of
/// every dispatcher-touching test class) touches
/// <c>Dispatcher.UIThread</c> from a dedicated background thread, so that
/// thread owns the process-wide dispatcher forever. The ownership has two
/// consequences the facts rely on:
/// <list type="bullet">
/// <item>Posted notifications (every <c>ObserveOnUiThread</c> ride) NEVER
/// execute spontaneously — the owner thread idles until a fact asks for a
/// drain, so every fact's pre-drain asserts see a frozen, deterministic
/// world (the chart and order book facts are marshal-invariant and never
/// drain at all).</item>
/// <item>A fact CAN observe marshaled writes deterministically via
/// <see cref="DrainAsync"/> — the pump records the drain completion after
/// <c>RunJobs</c> returns, so everything queued BEFORE the drain landed on
/// the owner thread and nothing after (the status bar notice facts pin
/// value transitions this way).</item>
/// </list>
/// Execution through the dispatcher requires the calling thread to own it
/// (<c>Dispatcher.VerifyAccess</c> inside job execution), so plain
/// <c>RunJobs</c> calls from test threads can never work — the pump is the
/// only drain path.
/// </summary>
internal static class AvaloniaDispatcherPump
{
    /// <summary>How long a fact's drain may stay unexecuted before the pump is deemed hung.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Signal the pump worker: at least one drain was requested.</summary>
    private static readonly SemaphoreSlim PumpRequested = new(initialCount: 0, maxCount: 1);

    /// <summary>Per-drain completion handles, consumed one RunJobs cycle each.</summary>
    private static readonly ConcurrentQueue<TaskCompletionSource> DrainRequests = new();

    /// <summary>The thread claiming (and forever holding) the dispatcher ownership; null before install.</summary>
    private static Thread? _pump;

    /// <summary>CAS gate: 1 once an install attempt started (only the winner installs).</summary>
    private static int _installStarted;

    /// <summary>Set once the ownership claim is complete (or failed) — late callers must not touch the dispatcher before the pump owns it.</summary>
    private static readonly ManualResetEventSlim OwnershipClaimed = new(initialState: false);

    #region Bootstrap

    /// <summary>
    /// Claim the process-wide dispatcher for the pump thread if not claimed
    /// yet. Idempotent and thread-safe (a CAS gate, no lock); every
    /// dispatcher-touching class calls it from its static constructor —
    /// xUnit loads one class at a time, so the install always precedes the
    /// first fact of the caller, no matter which class or filter ran first.
    /// </summary>
    internal static void EnsureInstalled()
    {
        if (Interlocked.CompareExchange(ref _installStarted, 1, 0) == 0)
        {
            try
            {
                InstallPump();
            }
            finally
            {
                // Even a failed install must unlock the late callers: the
                // dispatcher is the broken resource in that case, but a
                // hang would hide it.
                OwnershipClaimed.Set();
            }
        }
        else
        {
            // Another thread installs right now: wait until the ownership
            // claim is settled — touching the dispatcher before the pump
            // owns it would hand the ownership to the calling fact thread.
            OwnershipClaimed.Wait();
        }
    }

    private static void InstallPump()
    {
        using var ready = new ManualResetEventSlim(initialState: false);
        var pump = new Thread(RunPump)
        {
            IsBackground = true,
            Name = "avalonia-dispatcher-pump",
        };
        pump.Start();
        ready.Wait();
        _pump = pump;
        return;

        void RunPump()
        {
            try
            {
                // First touch claims the process-wide dispatcher for THIS
                // thread, deterministically before any fact.
                _ = Dispatcher.UIThread;
            }
            finally
            {
                ready.Set();
            }

            PumpLoop();
        }
    }

    /// <summary>Idle forever; execute exactly one drain cycle per request.</summary>
    private static void PumpLoop()
    {
        while (true)
        {
            PumpRequested.Wait();

            while (DrainRequests.TryDequeue(out var completion))
            {
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    // surfaced on the requesting fact's task, the pump stays
                    // alive to serve the next fact.
                    completion.TrySetException(ex);
                }
            }
        }
    }

    #endregion Bootstrap

    /// <summary>
    /// Execute every dispatcher job queued at this moment on the owner
    /// thread. Fails with <see cref="TimeoutException"/> if the pump does
    /// not finish in time (never in a healthy pipeline).
    /// </summary>
    /// <returns>The completed drain.</returns>
    internal static async Task DrainAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DrainRequests.Enqueue(completion);
        PumpRequested.Release();

        await completion.Task.WaitAsync(DrainTimeout);
    }
}
