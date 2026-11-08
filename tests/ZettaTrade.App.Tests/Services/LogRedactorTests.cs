namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The redactor's golden matrix (T-101): every catalog rule collapses its
/// secret shape into the marker while the key names and the surrounding log
/// structure survive, prose and identifiers never match, and the redaction is
/// idempotent — a line that already went through re-redacts to itself, so a
/// twice-fed pipeline can never double-mangle a display line.
/// </summary>
public sealed class LogRedactorTests
{
    #region Tests: the block rule

    /// <summary>The PEM block: the whole span collapses into one marker.</summary>
    [Fact]
    public void APemKeyBlock_CollapsesIntoOneMarker()
    {
        var line = "Key material installed: -----BEGIN RSA PRIVATE KEY-----MHQCAQEEIIabcdef-----END RSA PRIVATE KEY----- import done";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().NotContain("MHQCAQEEII", "the key's body never survives redaction");
        redacted.Should().Contain("Key material installed", "the framing around the block stays visible");
        redacted.Should().Contain("import done");
    }

    #endregion

    #region Tests: the value rules

    /// <summary>
    /// A JSON credential dump keeps the FIELD name and swaps the value for
    /// the marker (the line still reads as a log line).
    /// </summary>
    [Theory]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("apiSecret")]
    [InlineData("secret")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("authToken")]
    [InlineData("botToken")]
    [InlineData("password")]
    public void AJsonCredentialField_KeepsTheKeyName_SwapsTheValue(string key)
    {
        var line = $"UserProfile loaded: \"{key}\": \"the-real-secret-value\" end";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain($"\"{key}\": \"{LogRedactor.RedactedMarker}\"");
        redacted.Should().NotContain("the-real-secret-value");
    }

    /// <summary>Quoted assignments redact the same way (the marker re-quotes in the log's own shape).</summary>
    [Fact]
    public void ASingleQuotedAssignment_RedactsItsValue()
    {
        var line = "Auth attempt: 'apiSecret': 'hunter2hunter2' completed";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain($"'apiSecret': \"{LogRedactor.RedactedMarker}\"");
        redacted.Should().NotContain("hunter2hunter2");
    }

    /// <summary>The query-string credential shape redacts each keyed value; a non-credential key never matches.</summary>
    [Fact]
    public void QueryCredentials_RedactPerKey_AndNonCredentialKeysStay()
    {
        var line = "GET https://api/test?api_key=abc123def456&page_token=next7&signature=zzz done";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain("?api_key=" + LogRedactor.RedactedMarker);
        redacted.Should().Contain("&signature=" + LogRedactor.RedactedMarker);
        redacted.Should().Contain("page_token=next7", "the ?/& anchor keeps non-credential keys visible");
    }

    /// <summary>The literal header-assignment shape (exchange SDKs) redacts its value.</summary>
    [Fact]
    public void AHeaderAssignmentCredential_RedactsItsValue()
    {
        var line = "Request signed with X-MBX-APIKEY=deadbeefdeadbeef123 done";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain("X-MBX-APIKEY=" + LogRedactor.RedactedMarker);
        redacted.Should().NotContain("deadbeefdeadbeef123");
    }

    /// <summary>The complete Authorization header pair redacts behind its scheme.</summary>
    [Fact]
    public void AnAuthorizationHeader_RedactsBehindTheScheme()
    {
        var line = "Sending Authorization: Bearer abcd1234efgh5678 along";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain("Authorization: " + LogRedactor.RedactedMarker);
        redacted.Should().NotContain("abcd1234efgh5678");
    }

    /// <summary>The industry token shapes redact as tokens (the T-117 probe shapes).</summary>
    [Theory]
    [InlineData("sk-proj-abcdef1234567890abcdef12")]
    [InlineData("ghp_0123456789abcdef0123456789abcdef1234")]
    [InlineData("AKIAIOSFODNN7EXAMPLE912345678")]
    [InlineData("AIzaSyA0123456789abcdefghijklmnopqrstuv")]
    [InlineData("xoxb-1234567890-abcdef")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.SignaturePart1")]
    public void AnIndustryTokenShape_Redacts(string token)
    {
        var line = $"The client initialized with {token} and left.";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().NotContain(token[5..], "the token's midsection never survives");
        redacted.Should().Contain(LogRedactor.RedactedMarker);
    }

    #endregion

    #region Tests: the opaque-blob rule's edges

    /// <summary>A 32+ mixed-alphabet-and-digit run is a machine value and redacts.</summary>
    [Fact]
    public void AnOpaqueMixedAlphabetRun_Redacts()
    {
        var mixed = "ab12cd34ef56gh78ij90kl12mn34op56";
        var line = $"Session restored for {mixed} today.";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().NotContain(mixed);
    }

    /// <summary>Pure-letter identifiers survive byte-identical.</summary>
    [Fact]
    public void AnAllLetterIdentifier_NeverRedacts()
    {
        var line = "The adapter OrderBookSymbolCache settled";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Be(line, "prose and identifiers must survive byte-identical");
    }

    /// <summary>Pure-digit numbers survive (they never read as secrets).</summary>
    [Fact]
    public void AnAllDigitRun_NeverRedacts()
    {
        var line = "Position 1234567890123456789012345678901234 units";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Contain("1234567890123456789012345678901234", "numbers never read as secrets");
    }

    /// <summary>Timestamps and symbols never trip the blob rule: separators break the runs.</summary>
    [Fact]
    public void ASymbolAndTimestamp_NeverRedact()
    {
        var line = "2026-11-08 09:00:00.123 +00:00 [INF] Tape.Ticker BTC/USDT 64000.5";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Be(line, "the log's own structure must never mangle");
    }

    #endregion

    #region Tests: idempotence

    /// <summary>An already-redacted line survives a second pass unchanged.</summary>
    [Fact]
    public void AnAlreadyRedactedLine_IsIdempotent()
    {
        var once = LogRedactor.Redact("""loaded "apiKey": "super-value-123456" ok""");
        var twice = LogRedactor.Redact(once);

        once.Should().Contain(LogRedactor.RedactedMarker);
        twice.Should().Be(once, "the marker characters sit outside every pattern's charset");
    }

    #endregion

    #region Tests: the harness guards

    [Fact]
    public void AnEmptyLine_PassesThrough()
    {
        LogRedactor.Redact(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void APlainSentence_NeverRedacts()
    {
        var line = "The strategy stalled: start 2026-01-01 at 32 seconds behind.";

        var redacted = LogRedactor.Redact(line);

        redacted.Should().Be(line);
    }

    #endregion
}
