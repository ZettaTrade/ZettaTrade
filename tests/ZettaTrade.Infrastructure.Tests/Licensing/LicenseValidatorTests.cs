namespace ZettaTrade.Infrastructure.Tests.Licensing;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Infrastructure.Tests.RateLimiting;

/// <summary>
/// The RSA-SHA256 validator matrix (T-132): one test key pair issues the
/// keys of every family — a valid key verifies, a flipped byte breaks the
/// signature, a foreign product or plugin id refuses, an expired key
/// refuses, an unparsable token or an unparsable claims document refuses,
/// and an unconfigured (or unparsable) verification key refuses everything
/// before any signature check runs. The keys live in the tests only — no
/// real material ever ships.
/// </summary>
public sealed class LicenseValidatorTests
{
    private static readonly RSA KeyPair = RSA.Create(2048);

    private static readonly string KeyPem = ToPem(KeyPair.ExportSubjectPublicKeyInfo());

    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoVerificationKeyConfigured_RefusesEverything()
    {
        var settings = new TestSettingsService();
        var validator = new LicenseValidator(
            settings,
            NullLogger<LicenseValidator>.Instance);

        var status = await validator.ValidateAsync("chart-lens", "zt1.aaa.bbb");

        status.Should().Be(LicenseValidationStatus.Unconfigured);
    }

    [Fact]
    public async Task UnparsableVerificationKeyConfigured_RefusesEverything()
    {
        var settings = Settings(keyVerification: "definitely not a PEM");
        var validator = new LicenseValidator(
            settings,
            NullLogger<LicenseValidator>.Instance);

        var status = await validator.ValidateAsync(
            "chart-lens",
            Issue("chart-lens", validity: TimeSpan.FromHours(1)));

        status.Should().Be(LicenseValidationStatus.Unconfigured);
    }

    [Fact]
    public async Task ValidKey_Verifies()
    {
        var validator = Validator();

        var status = await validator.ValidateAsync(
            "chart-lens",
            Issue("chart-lens", validity: TimeSpan.FromHours(1)));

        status.Should().Be(LicenseValidationStatus.Valid);
    }

    [Fact]
    public async Task FlippedPayloadByte_BreaksTheSignature()
    {
        var validator = Validator();
        var token = Issue("chart-lens", validity: TimeSpan.FromHours(1));
        var tampered = TamperPayload(token);

        var status = await validator.ValidateAsync("chart-lens", tampered);

        status.Should().Be(LicenseValidationStatus.Tampered);
    }

    [Fact]
    public async Task ForeignSignatureKey_BreaksTheVerification()
    {
        var validator = Validator();

        // A second, unrelated key pair "signs" the payload: the verifier's
        // configured public key belongs to a different issuer, so the
        // signature check fails exactly like a tampered payload.
        var token = EncodeToken(
            SerializePayload(product: "ZettaTrade", pluginId: "chart-lens", expiry: DateTimeOffset.UtcNow.AddHours(1)),
            RSA.Create(2048));

        var status = await validator.ValidateAsync("chart-lens", token);

        status.Should().Be(LicenseValidationStatus.Tampered);
    }

    [Fact]
    public async Task KeyForAnotherProduct_Refuses()
    {
        var validator = Validator();

        var status = await validator.ValidateAsync(
            "chart-lens",
            Issue("chart-lens", product: "SomeOtherProduct", validity: TimeSpan.FromHours(1)));

        status.Should().Be(LicenseValidationStatus.WrongProduct);
    }

    [Fact]
    public async Task KeyForAnotherPluginId_Refuses()
    {
        var validator = Validator();

        var status = await validator.ValidateAsync(
            "order-ladder",
            Issue("chart-lens", validity: TimeSpan.FromHours(1)));

        status.Should().Be(LicenseValidationStatus.WrongProduct);
    }

    [Fact]
    public async Task ExpiredKey_Refuses()
    {
        var validator = Validator();

        var status = await validator.ValidateAsync(
            "chart-lens",
            Issue("chart-lens", validity: TimeSpan.FromHours(-1)));

        status.Should().Be(LicenseValidationStatus.Expired);
    }

    [Fact]
    public async Task NotATokenShape_Refuses()
    {
        var validator = Validator();

        var status = await validator.ValidateAsync("chart-lens", "just a pasted word");

        status.Should().Be(LicenseValidationStatus.Malformed);
    }

    [Fact]
    public async Task SignedButNotAClaimsDocument_Refuses()
    {
        var validator = Validator();
        var token = EncodeToken(
            JsonSerializer.SerializeToUtf8Bytes(new[] { "an", "array", "not", "the", "claims" }),
            KeyPair);

        var status = await validator.ValidateAsync("chart-lens", token);

        status.Should().Be(LicenseValidationStatus.Malformed);
    }

    #region Stubs

    private static LicenseValidator Validator() => new(
        Settings(keyVerification: KeyPem),
        NullLogger<LicenseValidator>.Instance);

    private static TestSettingsService Settings(string keyVerification)
    {
        var settings = new TestSettingsService();
        settings.SetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.VerificationKey,
            keyVerification).GetAwaiter().GetResult();
        return settings;
    }

    private static string ToPem(byte[] spki)
    {
        var base64 = Convert.ToBase64String(spki);
        return $"""
            -----BEGIN PUBLIC KEY-----
            {LineWrap(base64, 64)}
            -----END PUBLIC KEY-----
            """;
    }

    private static string LineWrap(string text, int width) =>
        string.Join('\n', Enumerable.Range(0, (text.Length + width - 1) / width)
            .Select(i => i * width)
            .Select(offset => text.Substring(offset, Math.Min(width, text.Length - offset))));

    private static string Issue(
        string pluginId,
        string product = "ZettaTrade",
        TimeSpan validity = default) =>
        EncodeToken(
            SerializePayload(product, pluginId, DateTimeOffset.UtcNow.Add(validity)),
            KeyPair);

    private static byte[] SerializePayload(
        string product,
        string pluginId,
        DateTimeOffset expiry) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new LicenseKeyPayload(product, pluginId, "tester", Issued, expiry),
            LicenseJson.Options);

    private static string EncodeToken(byte[] payload, RSA signingKey) =>
        LicenseToken.Encode(
            payload,
            signingKey.SignData(
                payload,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

    private static string TamperPayload(string token)
    {
        var segments = token.Split('.');
        var payload = Convert.FromBase64String(
            segments[1].Replace('-', '+').Replace('_', '/') + Padding(segments[1].Length));
        payload[0] = (byte)(payload[0] ^ 0xFF);
        return $"zt1.{Base64UrlEncode(payload)}.{segments[2]}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Padding(int length) => (length % 4) switch
    {
        2 => "==",
        3 => "=",
        _ => string.Empty,
    };

    #endregion
}
