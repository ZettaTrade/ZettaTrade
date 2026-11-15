namespace ZettaTrade.App.Tests.Services;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The pre-release-aware version comparison's battery (T-213): the
/// parse shapes (the running and remote spellings, the v-prefix and
/// build-metadata tolerances, the honest refusals) and the ordering
/// (the core numbers first, the semver pre-release rule, the numeric
/// alpha ranking that string order would break at 9 vs 10).
/// </summary>
public sealed class UpdateVersionComparerTests
{
    #region Tests: parse

    /// <summary>The spellings the project and the tags actually carry parse to the same comparable shape.</summary>
    [Fact]
    public void TheProjectSpellings_ParseToTheComparableShape()
    {
        UpdateVersionComparer.Parse("0.1.0-alpha.158").Should().Be(
            new UpdateVersion(0, 1, 0, PreReleaseKind.Alpha, 158),
            "the running version's raw informational spelling parses");
        UpdateVersionComparer.Parse("v0.1.0-alpha.9").Should().Be(
            new UpdateVersion(0, 1, 0, PreReleaseKind.Alpha, 9),
            "the tag's leading v is decoration, not part of the version");
        UpdateVersionComparer.Parse("0.1.0-alpha.159+24ca2c8").Should().Be(
            new UpdateVersion(0, 1, 0, PreReleaseKind.Alpha, 159),
            "build metadata never ranks");
        UpdateVersionComparer.Parse(" 0.1.0-alpha.160 ").Should().Be(
            new UpdateVersion(0, 1, 0, PreReleaseKind.Alpha, 160),
            "whitespace is noise");
        UpdateVersionComparer.Parse("1.2.3-beta.2").Should().Be(
            new UpdateVersion(1, 2, 3, PreReleaseKind.Beta, 2),
            "the beta family the checker offers on the beta channel parses (T-258)");
        UpdateVersionComparer.Parse("v1.0.0-beta.10").Should().Be(
            new UpdateVersion(1, 0, 0, PreReleaseKind.Beta, 10),
            "the tag decorations carry over to the beta family too");
        UpdateVersionComparer.Parse("1.2.3").Should().Be(
            new UpdateVersion(1, 2, 3, PreReleaseKind.None, 0),
            "a stable spelling carries no pre-release");
        UpdateVersionComparer.Parse("0.2.0").Should().Be(
            new UpdateVersion(0, 2, 0, PreReleaseKind.None, 0),
            "the short core trio is complete (the missing numbers default to zero)");
        UpdateVersionComparer.Parse("1.0").Should().Be(
            new UpdateVersion(1, 0, 0, PreReleaseKind.None, 0),
            "a two-part core is complete");
    }

    /// <summary>An unparsable spelling yields no version — the checker skips the tag, never guesses.</summary>
    [Fact]
    public void UnparsableSpellings_YieldNothing()
    {
        UpdateVersionComparer.Parse(null).Should().BeNull("no fact is no version");
        UpdateVersionComparer.Parse(string.Empty).Should().BeNull();
        UpdateVersionComparer.Parse("   ").Should().BeNull();
        UpdateVersionComparer.Parse("v").Should().BeNull("a bare decoration is not a version");
        UpdateVersionComparer.Parse("alpha.158").Should().BeNull("a pre-release without a core is not a version");
        UpdateVersionComparer.Parse("1.2.3-rc.1").Should().BeNull(
            "only the alpha and beta families the project ships are ranked — a foreign pre-release stays unparsable");
        UpdateVersionComparer.Parse("1.2.3-beta").Should().BeNull("a beta tag without a number is not a version");
        UpdateVersionComparer.Parse("1.2.3-beta.x").Should().BeNull("a non-numeric beta is not a version");
        UpdateVersionComparer.Parse("1.2.3.4").Should().BeNull("a four-part core is not a semantic version");
        UpdateVersionComparer.Parse("1.2.x").Should().BeNull("a letter core is not a version");
        UpdateVersionComparer.Parse("1.2.3-alpha.x").Should().BeNull("a non-numeric alpha is not a version");
        UpdateVersionComparer.Parse("-1.2.3").Should().BeNull("no negative cores");
    }

    #endregion

    #region Tests: compare

    /// <summary>The core numbers rank first, by number (never by string).</summary>
    [Fact]
    public void TheCoreNumbers_RankNumerically()
    {
        UpdateVersionComparer.IsNewer("0.1.0-alpha.158", "0.2.0-alpha.1").Should().BeTrue(
            "the minor number outranks the alpha number");
        UpdateVersionComparer.IsNewer("0.1.0-alpha.158", "1.0.0-alpha.1").Should().BeTrue(
            "the major number outranks everything");
        UpdateVersionComparer.IsNewer("0.1.0-alpha.158", "0.1.0-alpha.157").Should().BeFalse(
            "an older alpha is older");
    }

    /// <summary>The semver pre-release rule: a stable outranks any pre-release of the same core.</summary>
    [Fact]
    public void AStableVersion_OutranksItsPreReleases()
    {
        UpdateVersionComparer.IsNewer("0.1.0-alpha.999", "0.1.0").Should().BeTrue(
            "the stable of the same core outranks every alpha — even the very last one");
        UpdateVersionComparer.IsNewer("0.1.0", "0.1.0-alpha.1").Should().BeFalse(
            "an alpha never outranks its own stable");
        UpdateVersionComparer.IsNewer("0.1.0", "0.1.0").Should().BeFalse(
            "equal spellings announce nothing");
    }

    /// <summary>Two pre-releases of the same core rank by their kind and their NUMBER — the string order breaks at 9 vs 10.</summary>
    [Fact]
    public void TwoPreReleases_RankByTheAlphaNumber_NeverByTheString()
    {
        UpdateVersionComparer.IsNewer("0.1.0-alpha.9", "0.1.0-alpha.10").Should().BeTrue(
            "ten outranks nine — the numeric ranking the string comparison would flip");
        UpdateVersionComparer.IsNewer("0.1.0-alpha.10", "0.1.0-alpha.9").Should().BeFalse();
        UpdateVersionComparer.IsNewer("0.1.0-alpha.158", "0.1.0-alpha.158").Should().BeFalse(
            "the running version never announces itself");
    }

    /// <summary>The beta family ranks between its alpha and its stable (the semver identifier order), T-258.</summary>
    [Fact]
    public void ABeta_RanksBetweenItsAlphaAndItsStable()
    {
        UpdateVersionComparer.IsNewer("0.2.0-alpha.99", "0.2.0-beta.1").Should().BeTrue(
            "a beta outranks any alpha of the same core");
        UpdateVersionComparer.IsNewer("0.2.0-beta.1", "0.2.0-alpha.99").Should().BeFalse(
            "an alpha never outranks a beta of the same core");
        UpdateVersionComparer.IsNewer("0.2.0-beta.999", "0.2.0").Should().BeTrue(
            "the stable of the same core still outranks every beta");
        UpdateVersionComparer.IsNewer("0.2.0-beta.9", "0.2.0-beta.10").Should().BeTrue(
            "two betas rank by their number, never by the string");
        UpdateVersionComparer.IsNewer("0.2.0-beta.1", "0.3.0-alpha.1").Should().BeTrue(
            "the core numbers rank first, before any pre-release kind");
    }

    /// <summary>The comparison helper refuses to rank against an unparsable side.</summary>
    [Fact]
    public void AnUnparsableSide_NeverRanks()
    {
        UpdateVersionComparer.IsNewer(null, "0.1.0-alpha.159").Should().BeFalse(
            "an unparsable running version cannot be outranked by the comparison");
        UpdateVersionComparer.IsNewer("0.1.0-alpha.158", "not-a-version").Should().BeFalse(
            "an unparsable candidate never announces");
    }

    #endregion
}
