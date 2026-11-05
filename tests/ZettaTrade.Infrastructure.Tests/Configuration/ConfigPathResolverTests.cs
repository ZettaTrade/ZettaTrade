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
}
