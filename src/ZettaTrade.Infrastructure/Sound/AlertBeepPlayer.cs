namespace ZettaTrade.Infrastructure.Sound;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// The cross-platform beep the alerts module fires with (T-096, T-220): the
/// kernel32 console beep on Windows, the desktop sound kit's own player on
/// macOS, and the freedesktop/ALSA players on Linux. The T-220 shape: the
/// Windows tone is a synchronous ~180 ms kernel32 call, so <see cref="Play"/>
/// never runs it on the caller's thread — it dispatches the playback to the
/// worker pool and returns promptly, and the engine's consumer loop (the fire
/// path) never waits for a tone. The dispatched work is fully degradable in
/// its own body: every platform fault logs through this player's own warning
/// lane, so a failed beep stays a logged warning, never an unobserved fault,
/// never a broken evaluation — exactly the T-096 contract, relocated to where
/// the playback now runs.
/// </summary>
public sealed class AlertBeepPlayer : IAlertSoundPlayer
{
    #region Private Fields

    /// <summary>The Windows beep's single tone (A5 — a plain terminal bell grade).</summary>
    private const uint WindowsFrequencyHz = 880;

    /// <summary>How long the Windows beep holds (milliseconds).</summary>
    private const uint WindowsDurationMillis = 180;

    /// <summary>The macOS player: Apple's own bundled sound player executable.</summary>
    private const string MacBeepProgram = "/usr/bin/afplay";

    /// <summary>The macOS sound: a system-bell voice present on every supported macOS install.</summary>
    private const string MacBeepSound = "/System/Library/Sounds/Ping.aiff";

    /// <summary>The Linux attempts in preference order: the freedesktop desktop player first, the ALSA console player second.</summary>
    private static readonly (string Program, string Sound)[] LinuxBeeps =
    [
        ("/usr/bin/paplay", "/usr/share/sounds/freedesktop/stereo/complete.oga"),
        ("/usr/bin/aplay", "/usr/share/sounds/alsa/Front_Center.wav"),
    ];

    /// <summary>The player's own degrade lane: the dispatched playback's faults log here.</summary>
    private readonly ILogger<AlertBeepPlayer> _logger;

    /// <summary>Where the dispatched playback runs (the worker pool in production; the test seam injects its own).</summary>
    private readonly Func<Action, Task> _dispatch;

    /// <summary>The platform attempt the playback wraps (the real tone in production; the test seam injects its own).</summary>
    private readonly Action _attempt;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the beep player over its logger; the playback dispatches to the worker pool.</summary>
    /// <param name="logger">The player's own degrade lane (the dispatched playback's faults log here).</param>
    public AlertBeepPlayer(ILogger<AlertBeepPlayer> logger)
        : this(logger, static work => Task.Run(work), PlayPlatformTone)
    {
    }

    /// <summary>
    /// The T-220 test seam: the tests inject where the playback runs and what
    /// the platform attempt is, so the beep facts await the playback instead
    /// of racing it and never sound a real tone.
    /// </summary>
    internal AlertBeepPlayer(ILogger<AlertBeepPlayer> logger, Func<Action, Task> dispatch, Action attempt)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
    }

    #endregion Constructors

    /// <inheritdoc/>
    public void Play()
    {
        // The T-220 fire path: the tone dispatches and the call returns
        // promptly — a firing burst never pays the playback's duration. The
        // returned handle is dropped knowingly: the dispatched body degrades
        // every fault into this player's own warning lane below, so no task
        // can ever carry an unobserved fault, and no caller ever waits for a
        // tone (a burst keeps every playback running to its own logged end).
        _dispatch(PlaybackWork);
    }

    /// <summary>
    /// The dispatched playback body (runs where the dispatch sends it): the
    /// platform attempt wrapped whole — every fault logs through this
    /// player's own warning lane, so a failed beep degrades exactly as the
    /// T-096 contract says and the dropped dispatch handle can never carry
    /// an unobserved fault.
    /// </summary>
    private void PlaybackWork()
    {
        try
        {
            _attempt();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The alert beep's playback failed and was degraded away");
        }
    }

    /// <summary>
    /// The real platform attempt: the kernel32 console beep on Windows, the
    /// desktop sound kit's own player on macOS, the freedesktop/ALSA players
    /// on Linux. A failing platform attempt throws for the dispatched body
    /// to degrade through the player's own warning lane.
    /// </summary>
    private static void PlayPlatformTone()
    {
        if (OperatingSystem.IsWindows())
        {
            if (WindowsInterop.Beep(WindowsFrequencyHz, WindowsDurationMillis) == 0)
            {
                throw new InvalidOperationException("kernel32 reported a failed Windows beep.");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            StartDetached(MacBeepProgram, MacBeepSound);
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var (program, sound) in LinuxBeeps)
            {
                if (TryStartDetached(program, sound))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "No system bell player was found on this Linux installation (paplay and aplay both failed).");
        }
        else
        {
            throw new PlatformNotSupportedException("The alert beep has no implementation for this platform.");
        }
    }

    /// <summary>Start one detached player process; the OS owns the playback and nobody waits on it (the dispatched body returns as soon as the player spawns).</summary>
    /// <exception cref="InvalidOperationException">The player could not be started at all (a missing binary, a wedged session).</exception>
    private static void StartDetached(string program, string sound)
    {
        if (!TryStartDetached(program, sound))
        {
            throw new InvalidOperationException($"The beep player '{program}' could not be started.");
        }
    }

    /// <summary>
    /// Best-effort start: false when the process could not spawn (a missing
    /// binary, an unresponsive session); the boolean candidate ladder is the
    /// Linux players' fallback, the overall fault is the final throw. No
    /// secrets ride here — the arguments are the player's own fixed sound
    /// file paths.
    /// </summary>
    private static bool TryStartDetached(string program, string sound)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(sound);
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The candidate itself failed to spawn: the ladder continues with
            // the next player, and the final overall throw (or the player's
            // own degrade lane) carries the honest outcome — this seam keeps
            // no logger of its own.
            return false;
        }
    }
}

/// <summary>The platform tone the Windows beep rides (a classic extern import; BOOL is a plain int here).</summary>
internal static class WindowsInterop
{
    [DllImport("kernel32.dll", EntryPoint = "Beep", SetLastError = false)]
    internal static extern int Beep(uint frequencyHz, uint durationMillis);
}
