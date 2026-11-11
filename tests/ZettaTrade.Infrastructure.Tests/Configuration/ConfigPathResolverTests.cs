namespace ZettaTrade.Infrastructure.Tests.Configuration;

using System;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Constants;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The XDG path resolution matrix (BUG-044): unset (null) AND empty-string
/// variables must both fall back to the platform defaults — the pre-fix
/// `??` caught only null, so an empty <c>XDG_CONFIG_HOME</c>/"etc." fell
/// through and <c>Path.Combine</c> produced CWD-relative paths (broken
/// locations in containers and scripts that export variables without
/// values). A non-empty value still wins as before; a whitespace-only
/// value counts as unset because it is a relative path nothing could
/// create or write through. The facts run on Windows (where the CI runs),
/// so the OS-guarded resolvers never reach their XDG branches here and the
/// env values are driven through the internal facts seam the production
/// resolvers themselves call — each resolved directory pins its own
/// variable value and its own default composition.
/// </summary>
public sealed class ConfigPathResolverTests
{
    #region Broken Values

    public static TheoryData<string?> BrokenValues => new()
    {
        null,
        string.Empty,
        "   ",
    };

    #endregion Broken Values

    #region The Fallback Matrix

    [Theory]
    [MemberData(nameof(BrokenValues))]
    public void TheXdgDirectories_UnsetOrEmptyVariables_FallBackToThePlatformDefaults(
        string? envValue)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Each of the three resolved directories pins the value of ITS OWN
        // variable and the composition of ITS OWN spec default.
        ConfigPathResolver.LinuxConfigRoot(envValue).Should().Be(
            Path.Combine(profile, ".config", ApplicationConstants.ApplicationName),
            "XDG_CONFIG_HOME unset or empty falls back to ~/.config");
        ConfigPathResolver.LinuxDataRoot(envValue).Should().Be(
            Path.Combine(profile, ".local", "share", ApplicationConstants.ApplicationName),
            "XDG_DATA_HOME unset or empty falls back to ~/.local/share");
        ConfigPathResolver.LinuxStateRoot(envValue).Should().Be(
            Path.Combine(profile, ".local", "state", ApplicationConstants.ApplicationName),
            "XDG_STATE_HOME unset or empty falls back to ~/.local/state");
    }

    [Fact]
    public void TheXdgDirectories_AValidVariable_WinsAsBefore()
    {
        const string home = "/srv/xdg";

        ConfigPathResolver.LinuxConfigRoot(home).Should().Be(
            Path.Combine(home, ApplicationConstants.ApplicationName),
            "a non-empty XDG_CONFIG_HOME keeps winning");
        ConfigPathResolver.LinuxDataRoot(home).Should().Be(
            Path.Combine(home, ApplicationConstants.ApplicationName),
            "a non-empty XDG_DATA_HOME keeps winning");
        ConfigPathResolver.LinuxStateRoot(home).Should().Be(
            Path.Combine(home, ApplicationConstants.ApplicationName),
            "a non-empty XDG_STATE_HOME keeps winning");
    }

    /// <summary>
    /// The shared fallback rule itself, driven with the exact raw values an
    /// environment could carry, pinned per resolution target: full paths
    /// win; null, empty and whitespace all land on the given default.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenValues))]
    public void ResolveXdgRoot_ABrokenValue_LandsOnTheDefault(string? envValue)
    {
        var defaultRoot = Path.Combine("defaults", "here");

        ConfigPathResolver.ResolveXdgRoot(envValue, defaultRoot).Should().Be(
            Path.Combine("defaults", "here", ApplicationConstants.ApplicationName));
    }

    #endregion The Fallback Matrix

    #region Windows Surface

    /// <summary>
    /// The branch THIS process runs (Windows CI), pinned so the BUG-044
    /// refactor cannot drift the non-XDG roots: config under the roaming
    /// profile, data and logs under the local one.
    /// </summary>
    [Fact]
    public void TheWindowsDirectories_ResolveUnderTheProfileFolders()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        ConfigPathResolver.ConfigDirectory.Should().Be(
            Path.Combine(appData, ApplicationConstants.ApplicationName, "config"));
        ConfigPathResolver.DataDirectory.Should().Be(
            Path.Combine(localAppData, ApplicationConstants.ApplicationName, "data"));
        ConfigPathResolver.LogDirectory.Should().Be(
            Path.Combine(localAppData, ApplicationConstants.ApplicationName, "logs"));
    }

    #endregion Windows Surface

    #region The Profile Override (T-141)

    /// <summary>
    /// The profile-override rule itself, driven with the exact raw values an
    /// environment could carry (the same matrix the XDG facts run): null,
    /// empty and whitespace-only land on the given default.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenValues))]
    public void ApplyProfileRoot_ABrokenValue_LandsOnTheDefault(string? envValue)
    {
        var defaultRoot = Path.Combine("defaults", "here");

        ConfigPathResolver.ApplyProfileRoot(envValue, defaultRoot).Should().Be(
            Path.Combine("defaults", "here"),
            "an unset, empty or whitespace-only override counts as absent");
    }

    [Fact]
    public void ApplyProfileRoot_AValidDirectory_ReplacesTheRootWholesale()
    {
        ConfigPathResolver.ApplyProfileRoot(
            Path.Combine("soak", "sandbox"),
            Path.Combine("defaults", "here")).Should().Be(
            Path.Combine("soak", "sandbox"),
            "the sandbox IS the profile root: config, data and logs resolve as its children");
    }

    /// <summary>
    /// The env leg the production roots themselves read: with the variable
    /// set, every resolved directory lands inside the sandbox — the whole
    /// profile (settings, secrets, databases, logs) is absorbed and the
    /// user's real profile stays invisible to the process. The fact
    /// restores the environment in its own finally, so the Windows-surface
    /// fact above never observes a leak.
    /// </summary>
    [Fact]
    public void ThePublicDirectories_TheOverrideVariableSet_ResolveInsideTheSandbox()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "t141-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(ConfigPathResolver.ProfileOverrideEnvironmentVariable, sandbox);
        try
        {
            ConfigPathResolver.ConfigDirectory.Should().Be(
                Path.Combine(sandbox, "config"),
                "the pinned profile resolves its config (settings, themes) inside the sandbox");
            ConfigPathResolver.DataDirectory.Should().Be(
                Path.Combine(sandbox, "data"),
                "the pinned profile resolves its data (secrets, databases, plugins) inside the sandbox");
            ConfigPathResolver.LogDirectory.Should().Be(
                Path.Combine(sandbox, "logs"),
                "the pinned profile resolves its logs inside the sandbox");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPathResolver.ProfileOverrideEnvironmentVariable, null);
        }
    }

    #endregion The Profile Override (T-141)
}
