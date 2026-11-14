using Avalonia;
using ReactiveUI.Avalonia;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.SingleInstance;

namespace ZettaTrade.App;

/// <summary>
/// Application entry point.
/// </summary>
class Program
{
    /// <summary>The owned claim held for the whole process lifetime (releasing it early would let a concurrent start build a second app).</summary>
    private static IDisposable? _processLifetimeClaim;

    /// <summary>
    /// Main entry point: the single-instance dispatch decides FIRST — an
    /// owned start builds the application normally, while a redirected
    /// (second) start never touches Avalonia at all: its outcome is
    /// already settled by the dispatch lane, and the process returns the
    /// mapped exit code right away.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var launch = SingleInstanceGate.Enter(args);
        switch (launch)
        {
            case LaunchOutcome.Redirected redirected:
                // A second start: the outcome is already settled (the
                // forward or the refusal happened in the dispatch lane and
                // possibly wrote its receipt over the pipe) — the process
                // exits promptly with the mapped code.
                DispatchLog.Information(
                    "Second start redirected: {ExitCode} ({Detail})",
                    redirected.ExitCode,
                    redirected.Detail);
                return (int)redirected.ExitCode;
            default:
                var owned = (LaunchOutcome.Owned)launch;
                DispatchLog.Information("Single-instance claim acquired; building the application");
                return RunDesktopLifetime(args, owned.InstanceClaim);
        }
    }

    /// <summary>
    /// Build Avalonia's application.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(rxui => { });

    /// <summary>
    /// The owned lane: hold the dispatch claim, run the desktop lifetime
    /// to its own end, and release the claim only when the process is
    /// leaving — a boot failure releases it too (fail-fast never wedges
    /// the next launch).
    /// </summary>
    private static int RunDesktopLifetime(string[] args, IDisposable instanceClaim)
    {
        _processLifetimeClaim = instanceClaim;
        // T-237: the persistence models pre-warm — the EF model builds kick
        // off the composition walk, racing the UI bootstrap on worker
        // threads, so the store batch's wall collapses to the per-store
        // migration checks. Throwaway in-memory contexts only: no
        // connection, no file, and a degraded build is invisible to
        // correctness (the walk's own first touch builds as before).
        ZettaTrade.Infrastructure.Persistence.PersistenceModelPrewarm.Start();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _processLifetimeClaim = null;
            instanceClaim.Dispose();
        }
    }
}
