namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The release-artifact grammar's battery (T-258): the T-125 delivery
/// pipeline's names are the only ones the updater resolves — an archive
/// spells the project's shape (<c>zettatrade-0.2.2-win-x64.zip</c>) and
/// the sums file spells the same prefix and version with the
/// <c>-sha256.txt</c> suffix in the <c>sha256sum -c</c> two-space
/// convention. Every resolution is a strict parse: a foreign name, a
/// wrong RID, a version mismatch or a sums file without the archive's
/// line answers the honest null the staging refuses on — never a guessed
/// substitute.
/// </summary>
public sealed class UpdateAssetCatalogTests
{
    #region Tests: the archive grammar

    /// <summary>An archive asset parses into its version and RID; the known extensions and the v-tag shape included.</summary>
    [Fact]
    public void AnArchiveName_ParsesIntoItsParts()
    {
        UpdateAssetCatalog.TryParseArchive("zettatrade-0.2.0-win-x64.zip", out var version, out var rid).Should().BeTrue();
        version.Should().Be(new UpdateVersion(0, 2, 0, PreReleaseKind.None, 0));
        rid.Should().Be("win-x64");

        UpdateAssetCatalog.TryParseArchive("zettatrade-9.9.9-linux-x64.tar.gz", out version, out rid).Should().BeTrue(
            "the Linux delivery rides the tar.gz spelling");
        rid.Should().Be("linux-x64");

        UpdateAssetCatalog.TryParseArchive("zettatrade-0.1.0-alpha.159-osx-arm64.zip", out version, out rid).Should().BeTrue(
            "a pre-release archive parses with its kind");
        version.PreKind.Should().Be(PreReleaseKind.Alpha);
        rid.Should().Be("osx-arm64");
    }

    /// <summary>Foreign names never parse — the updater resolves this project's artifacts only.</summary>
    [Fact]
    public void AForeignName_NeverParses()
    {
        UpdateAssetCatalog.TryParseArchive("zettatrade-0.2.0.zip", out _, out _).Should().BeFalse(
            "an archive without a RID is not a delivery artifact");
        UpdateAssetCatalog.TryParseArchive("zettatrade-0.2.0-win-x64.7z", out _, out _).Should().BeFalse(
            "an unpublished extension is not the delivery's spelling");
        UpdateAssetCatalog.TryParseArchive("other-0.2.0-win-x64.zip", out _, out _).Should().BeFalse("a foreign prefix is not ours");
        UpdateAssetCatalog.TryParseArchive("zettatrade-banana-win-x64.zip", out _, out _).Should().BeFalse(
            "an unparsable version is not a version");
        UpdateAssetCatalog.TryParseArchive("zettatrade-0.2.0-win-x64", out _, out _).Should().BeFalse(
            "an extensionless name is not an archive");
        UpdateAssetCatalog.TryParseArchive("zettatrade-0.2.0-sha256.txt", out _, out _).Should().BeFalse(
            "the sums file is the other family");
        UpdateAssetCatalog.TryParseArchive(string.Empty, out _, out _).Should().BeFalse();
    }

    /// <summary>The archive family check separates the two families and the noise.</summary>
    [Fact]
    public void TheArchiveFamilyCheck_SeparatesTheFamilies()
    {
        UpdateAssetCatalog.IsArchiveName("zettatrade-0.2.0-win-x64.zip").Should().BeTrue();
        UpdateAssetCatalog.IsArchiveName("zettatrade-0.2.0-sha256.txt").Should().BeFalse("the sums file is not an archive");
        UpdateAssetCatalog.IsArchiveName("source.tar.gz").Should().BeFalse();
    }

    /// <summary>The archive pick resolves the running RID's asset — the version matches by parse, the display spelling pinned.</summary>
    [Fact]
    public void TheArchivePick_ResolvesTheRunningRidsAsset()
    {
        var assets = new[]
        {
            new UpdateAsset("zettatrade-0.2.0-win-x64.zip", "https://example.test/zettatrade-0.2.0-win-x64.zip"),
            new UpdateAsset("zettatrade-0.2.0-linux-x64.tar.gz", "https://example.test/zettatrade-0.2.0-linux-x64.tar.gz"),
            new UpdateAsset("zettatrade-0.2.0-sha256.txt", "https://example.test/zettatrade-0.2.0-sha256.txt"),
        };

        UpdateAssetCatalog.PickArchive(assets, "0.2.0", ["win-x64"]).Should().Be(assets[0]);
        UpdateAssetCatalog.PickArchive(assets, "0.2.0", ["linux-x64", "win-x64"]).Should().Be(
            assets[1],
            "the first matching candidate wins");
        UpdateAssetCatalog.PickArchive(assets, "0.2.0", ["osx-arm64"]).Should().BeNull(
            "a RID the release does not carry is the honest no-asset answer");
    }

    /// <summary>A version mismatch or a display-spelling drift never ranks as this release's archive.</summary>
    [Fact]
    public void AVersionMismatch_NeverRanksAsTheRelease()
    {
        var assets = new[]
        {
            new UpdateAsset("zettatrade-0.2.1-win-x64.zip", "https://example.test/zettatrade-0.2.1-win-x64.zip"),
            new UpdateAsset("zettatrade-0.2.0-unknown-rid.zip", "https://example.test/zettatrade-0.2.0-unknown-rid.zip"),
        };

        UpdateAssetCatalog.PickArchive(assets, "0.2.0", ["win-x64"]).Should().BeNull(
            "another version's archive is not this release's archive");
        UpdateAssetCatalog.PickArchive(assets, "0.2.0", ["unknown-rid"]).Should().BeNull(
            "an unpublishable RID spelling never matches");
        UpdateAssetCatalog.PickArchive([], "0.2.0", ["win-x64"]).Should().BeNull("an asset-less release offers nothing");
        UpdateAssetCatalog.PickArchive(assets, "banana", ["win-x64"]).Should().BeNull(
            "an unparsable release version is no version at all");
    }

    #endregion

    #region Tests: the sums file

    /// <summary>The sums pick rides the exact prefix + version + <c>-sha256.txt</c> spelling.</summary>
    [Fact]
    public void TheSumsPick_RidesTheExactSpelling()
    {
        var assets = new[]
        {
            new UpdateAsset("zettatrade-0.2.0-win-x64.zip", "https://example.test/archive"),
            new UpdateAsset("zettatrade-0.2.0-sha256.txt", "https://example.test/sums"),
        };

        UpdateAssetCatalog.PickHashFile(assets, "0.2.0").Should().Be(assets[1]);
        UpdateAssetCatalog.PickHashFile(assets, "0.3.0").Should().BeNull("another version's sums file is not this release's");
        UpdateAssetCatalog.PickHashFile([], "0.2.0").Should().BeNull();
    }

    /// <summary>The two-space convention resolves the archive's listed hash; comments and foreign lines skip.</summary>
    [Fact]
    public void TheTwoSpaceConvention_ResolvesTheListedHash()
    {
        const string archiveName = "zettatrade-0.2.0-win-x64.zip";
        var text = string.Join(
            "\n",
            "# SHA-256 sums for zettatrade 0.2.0",
            "aaaa  zettatrade-0.2.0-linux-x64.tar.gz",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  " + archiveName,
            "bbbb  zettatrade-0.2.0-osx-x64.zip");

        UpdateAssetCatalog.ExpectedHashFor(text, archiveName).Should().Be(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "the archive's own line is the pinned expected hash");
    }

    /// <summary>CRLF line endings and the file's absence resolve honestly.</summary>
    [Fact]
    public void TheSumsText_IsLineEndingTolerant_AndHonestAboutAbsence()
    {
        const string archiveName = "zettatrade-0.2.0-win-x64.zip";
        var text = $"aaaa  other.zip\r\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  {archiveName}\r\n";
        UpdateAssetCatalog.ExpectedHashFor(text, archiveName).Should().NotBeNull("CRLF endings are noise");

        UpdateAssetCatalog.ExpectedHashFor(string.Empty, archiveName).Should().BeNull("an empty sums file lists nothing");
        UpdateAssetCatalog.ExpectedHashFor("hash  zettatrade-0.2.0-win-x64.zip", archiveName).Should().BeNull(
            "a malformed line is skipped, never guessed");
        UpdateAssetCatalog.ExpectedHashFor(text, "missing.zip").Should().BeNull("an unlisted file answers null");
    }

    /// <summary>The hash match is hex, case-insensitive and length-pinned.</summary>
    [Fact]
    public void TheHashMatch_IsCaseInsensitive_AndLengthPinned()
    {
        const string lower = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        UpdateAssetCatalog.HashMatches(lower, lower.ToUpperInvariant()).Should().BeTrue("hex case is noise");
        UpdateAssetCatalog.HashMatches(lower, lower).Should().BeTrue();
        UpdateAssetCatalog.HashMatches(null, lower).Should().BeFalse();
        UpdateAssetCatalog.HashMatches(lower, null).Should().BeFalse();
        UpdateAssetCatalog.HashMatches(lower, "short").Should().BeFalse("a wrong length is no hash");
        UpdateAssetCatalog.HashMatches(lower, new string('g', 64)).Should().BeFalse("non-hex is no hash");
    }

    #endregion

    #region Tests: the hashing

    /// <summary>The file hash rides the lowercase hex spelling of the well-known empty vector.</summary>
    [Fact]
    public async Task TheFileHash_IsTheLowercaseHexVector()
    {
        var path = NewTempPath();
        await File.WriteAllBytesAsync(path, [], CancellationToken.None);
        try
        {
            var hash = await UpdateAssetCatalog.ComputeSha256HexAsync(path, CancellationToken.None);
            hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            hash.All(Uri.IsHexDigit).Should().BeTrue("the sums convention spells hex");
            hash.Should().Be(hash.ToLowerInvariant(), "the sums convention spells lowercase");
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Helpers

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), "t258-hash-" + Guid.NewGuid().ToString("N"));

    #endregion
}
