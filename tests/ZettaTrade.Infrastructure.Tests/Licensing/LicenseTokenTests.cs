namespace ZettaTrade.Infrastructure.Tests.Licensing;

using System.Buffers.Text;
using System.Text;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The compact license-token codec matrix (T-132): the exact
/// <c>zt1.payload.signature</c> shape round-trips, and every wrong shape —
/// another prefix, the wrong segment count, an empty segment, undecodable
/// segment bytes, a blank input — refuses without touching crypto.
/// </summary>
public sealed class LicenseTokenTests
{
    [Fact]
    public void Encode_Parse_RoundTripsPayloadAndSignature()
    {
        var payload = Encoding.UTF8.GetBytes("""{"product":"ZettaTrade"}""");
        var signature = Encoding.UTF8.GetBytes("the-signature-bytes");

        var token = LicenseToken.Encode(payload, signature);

        token.Should().StartWith("zt1.");
        var parsed = LicenseToken.TryParse(token, out var parsedPayload, out var parsedSignature);

        parsed.Should().BeTrue();
        parsedPayload.Should().Equal(payload);
        parsedSignature.Should().Equal(signature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankToken_Refuses(string? token) =>
        LicenseToken.TryParse(token, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("zt2.aGVsbG8.dGhlcmU")]
    [InlineData("zettatrade.aGVsbG8.dGhlcmU")]
    [InlineData("A.aGVsbG8.dGhlcmU")]
    public void Parse_WrongPrefix_Refuses(string token) =>
        LicenseToken.TryParse(token, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("zt1.onlytwosegments")]
    [InlineData("zt1.a.b.c")]
    [InlineData("zt1..dGhlcmU")]
    [InlineData("zt1.aGVsbG8.")]
    public void Parse_WrongSegmentShape_Refuses(string token) =>
        LicenseToken.TryParse(token, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("zt1.not-base64!.dGhlcmU")]
    [InlineData("zt1.aGVsbG8./slashed-payload")]
    public void Parse_UndecodableSegment_Refuses(string token) =>
        LicenseToken.TryParse(token, out _, out _).Should().BeFalse();

    [Fact]
    public void Parse_SurroundingWhitespace_Tolerates()
    {
        var payload = Encoding.UTF8.GetBytes("p");
        var signature = Encoding.UTF8.GetBytes("s");

        var token = LicenseToken.Encode(payload, signature);
        var wrapped = $"  \n{token}\r\n \t";

        var parsed = LicenseToken.TryParse(wrapped, out var parsedPayload, out var parsedSignature);

        parsed.Should().BeTrue();
        parsedPayload.Should().Equal(payload);
        parsedSignature.Should().Equal(signature);
    }

    [Fact]
    public void Encode_UsesUnpaddedBase64Url_Alphabet()
    {
        var payload = Encoding.UTF8.GetBytes("bytes>?< with url-unsafe chars");

        var token = LicenseToken.Encode(payload, []);

        var segments = token.Split('.');
        segments[2].Should().BeEmpty();
        Base64Url.IsValid(segments[1]).Should().BeTrue();
        segments[1].Should().NotContain("=").And.NotContain("+").And.NotContain("/");
    }
}
