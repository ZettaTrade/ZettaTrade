namespace ZettaTrade.App.Services;

using System.Text.RegularExpressions;

/// <summary>
/// The log viewer's secret-shaped-string redactor (T-101). The viewer reads
/// the Serilog file sinks the way a user would, so every line passes THIS
/// gate before it reaches the tail ring, the surface or the excerpt export:
/// a secret that ever leaked into a log line must not surface through the
/// viewer either.
/// <para>
/// The catalog pins the KNOWN secret shapings this terminal actually
/// transports — the <c>ISecretStore</c>-style payloads (Bearer tokens,
/// credential key-value pairs, the query strings the MCP HTTP transport and
/// the exchange endpoints use) plus the industry token shapes (provider
/// prefixes, PEM key blocks, opaque 32+ character blobs). Redaction errs on
/// the safe side: a shape match is always redacted, structure anchors the
/// patterns (separators, quotes, fixed lengths) so prose never matches.
/// The key NAMES stay visible where structure allows — a redacted line
/// keeps reading as a log line.
/// </para>
/// <para>
/// Idempotent: a line that already went through the redactor behaves the
/// same the second time — the marker characters sit outside every
/// pattern's charset, so an already-redacted line re-redacts to itself.
/// </para>
/// </summary>
public static partial class LogRedactor
{
    #region Catalog

    /// <summary>The replacement marker every redaction renders.</summary>
    public const string RedactedMarker = "<redacted>";

    /// <summary>
    /// The PEM private-key block: the whole <c>-----BEGIN … PRIVATE KEY-----</c>
    /// <c>…-----END … PRIVATE KEY-----</c> span collapses into one marker
    /// (the body is the secret, the markers only identify it).
    /// </summary>
    [GeneratedRegex(
        @"-----BEGIN ([A-Z ]+)PRIVATE KEY-----[\s\S]*?-----END \1PRIVATE KEY-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex PemKeyBlock();

    /// <summary>
    /// The JSON credential field shape an <c>ISecretStore</c>-style payload
    /// dump prints: <c>"apiKey": "value"</c> (single or double quotes both
    /// tolerated; the value runs to the closing quote). The key name stays.
    /// </summary>
    [GeneratedRegex(
        """(?i)(["'](?:apiKey|api_key|apiSecret|api_secret|secret|token|accessToken|access_token|authToken|auth_token|botToken|bot_token|password)["']\s*[:=]\s*)["'][^"'\n]*["']""",
        RegexOptions.CultureInvariant)]
    private static partial Regex JsonCredentialField();

    /// <summary>
    /// The URL query-string credential shape the exchange endpoints and the
    /// MCP HTTP transport compose: the key sits right behind the query
    /// mark or behind the ampersand-joined parameters, so a separator
    /// followed by the credential name always anchors the match and
    /// <c>page_token</c> or similar never matches.
    /// </summary>
    [GeneratedRegex(
        @"(?i)([?&](?:api_key|apikey|api_secret|apisecret|access_token|auth_token|apiSignature|signature|token|secret)=)[^&\s""']]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    /// <summary>
    /// The header query-style assignments the exchange SDKs print with a
    /// literal <c>name=value</c> pair: <c>X-MBX-APIKEY=…</c>,
    /// <c>api_key=…</c> at a word boundary (anything but the
    /// query-string anchors already taken by the rule above).
    /// </summary>
    [GeneratedRegex(
        @"(?i)\b(X-MBX-APIKEY|X-Api-Key|X-API-Key|Kraken-API-Key|CB-ACCESS-KEY|api-key|apikey)=(\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderCredential();

    /// <summary>
    /// The Authorization header's scheme-value pair
    /// (<c>Authorization: Bearer …</c>, <c>Authorization: Basic …</c>, the
    /// MCP HTTP transport's credential line). The whole value pair is one
    /// credential once a scheme name follows.
    /// </summary>
    [GeneratedRegex(
        @"(?i)\bAuthorization\s*:\s*\S+\s+\S+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    /// <summary>
    /// Industry token prefixes (the T-117 probe shapes): OpenAI/Anthropic
    /// style <c>sk-</c>, GitHub <c>ghp_/gho_/ghs_/ghu_/ghr_/github_pat_</c>,
    /// AWS <c>AKIA…</c>, Google <c>AIza…</c>, Slack <c>xox?-</c>, and the
    /// three-segment JWT shape (a <c>eyJ</c> head and two further dotted
    /// segments).
    /// </summary>
    [GeneratedRegex(
        @"(?:\bsk-[A-Za-z0-9_-]{20,}|\bgh[postru]_[A-Za-z0-9]{36}|\bgithub_pat_[A-Za-z0-9_]{22,255}|\bAKIA[A-Z0-9]{16}|\bAIza[A-Za-z0-9_-]{35}|\bxox[abprs]-[A-Za-z0-9-]{10,}|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderToken();

    /// <summary>
    /// Opaque high-entropy blobs: a 32+ character run over the token charset
    /// that mixes ASCII letters and digits (a machine-issued value — never
    /// prose, never a symbol, never a timestamp, which always carry
    /// separators). A digest-shaped value redacts too; a partially
    /// redacted log line beats a leaked key.
    /// </summary>
    [GeneratedRegex(
        @"[A-Za-z0-9+/=_-]{32,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueBlob();

    /// <summary>The text a PEM block collapses into.</summary>
    private const string PemRedactedMarker = RedactedMarker + " private key block>";

    #endregion Catalog

    #region Public API

    /// <summary>
    /// Redact every secret-shaped pattern out of one raw log line. The
    /// rules run in exact-keymark order (the block first so the value rules
    /// never see its internals); a line without any shape passes through
    /// byte-identical.
    /// </summary>
    /// <param name="line">The raw line (one logical line, no newline).</param>
    /// <returns>The display-safe line.</returns>
    public static string Redact(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var redacted = PemKeyBlock().Replace(line, PemRedactedMarker);
        redacted = AuthorizationHeader().Replace(redacted, static match =>
        {
            var scheme = match.Value.IndexOf(':');
            return $"{match.Value[..(scheme + 1)]} {RedactedMarker}";
        });
        redacted = JsonCredentialField().Replace(
            redacted,
            static match => $"{match.Groups[1].Value}\"{RedactedMarker}\"");
        redacted = QueryCredential().Replace(
            redacted,
            static match => $"{match.Groups[1].Value}{RedactedMarker}");
        redacted = HeaderCredential().Replace(
            redacted,
            static match => $"{match.Groups[1].Value}={RedactedMarker}");
        redacted = ProviderToken().Replace(redacted, RedactedMarker);
        redacted = OpaqueBlob().Replace(redacted, static match => IsOpaqueSecret(match.Value)
            ? RedactedMarker
            : match.Value);
        return redacted;
    }

    /// <summary>
    /// Whether one blob match is a secret-shaped opaque value: at least one
    /// ASCII letter and one ASCII digit (a mixed-alphabet machine value).
    /// Pure-letter runs (camelCase identifiers) and pure-digit runs (numeric
    /// ids) pass through.
    /// </summary>
    private static bool IsOpaqueSecret(string blob)
    {
        var letter = false;
        var digit = false;

        foreach (var ch in blob)
        {
            if (char.IsAsciiLetter(ch))
            {
                letter = true;
            }
            else if (char.IsAsciiDigit(ch))
            {
                digit = true;
            }
        }

        return letter && digit;
    }

    #endregion Public API
}
