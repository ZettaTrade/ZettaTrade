namespace ZettaTrade.Infrastructure.Tests.Plugins;

using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The SemVer gate goldens (T-131): the manifest version accepts the
/// SemVer 2 shapes — core, a prerelease part, a build part — and refuses
/// everything that is not one.
/// </summary>
public sealed class PluginSemVerTests
{
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("999.99.9")]
    [InlineData("1.2.3-pre")]
    [InlineData("1.2.3-pre.2.1")]
    [InlineData("1.2.3-alpha.beta+build.7")]
    [InlineData("1.2.3+build.7")]
    [Theory]
    public void TheSemVerShapesParse(string version)
    {
        PluginSemVer.TryParse(version, out var core).Should().BeTrue();

        core.Should().Be(version.Split('-')[0].Split('+')[0],
            "the canonical core is the numeric triple.");
    }

    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("not-semver")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3 PRE")]
    [Theory]
    public void TheNonShapesRefuse(string version)
    {
        PluginSemVer.TryParse(version, out var _).Should().BeFalse(
            "only the SemVer 2 shapes pass the gate.");
    }
}
