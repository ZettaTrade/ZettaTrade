namespace ZettaTrade.Infrastructure.Sound;

using System.Diagnostics;
using System.Runtime.InteropServices;
/// <summary>
/// The cross-platform beep the alerts module fires with (T-096): the
/// kernel32 console beep on Windows, the desktop sound kit's own player on
/// macOS, and the freedesktop/ALSA players on Linux. Sound failures are the
/// ENGINE's degrade lane's business — every fault here is a thrown
/// exception the fire pipeline catches and logs, so a beep never breaks an
/// evaluation, a toast or the app.
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

    #endregion Private Fields

    /// <inheritdoc/>
    public void Play()
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

    /// <summary>Start one detached player process; the OS owns the playback and the caller waits on nothing (the engine's fire path stays instant).</summary>
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
            // the next player, and the final overall throw (or the engine's
            // degrade log) carries the honest outcome — this seam keeps no
            // logger of its own.
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
