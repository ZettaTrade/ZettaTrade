namespace ZettaTrade.Infrastructure.Licensing;

using System.Buffers.Text;

/// <summary>
/// The compact license-token codec (T-132): the shape
/// <c>zt1.{payload}.{signature}</c> where both segments are
/// Base64Url without padding — a paste-safe, line-warp-safe string a
/// customer types once into the activation dialog. The payload segment is
/// the exact UTF-8 claims document the signature covers.
/// </summary>
public static class LicenseToken
{
    /// <summary>The token's version prefix (a token with any other prefix refuses).</summary>
    public const string Prefix = "zt1";

    private const int SegmentCount = 3;

    /// <summary>
    /// Renders the token: the prefix, the Base64Url payload and the
    /// Base64Url signature joined by dots.
    /// </summary>
    /// <param name="payload">The exact serialized claims bytes to sign and carry.</param>
    /// <param name="signature">The RSA-SHA256 signature over <paramref name="payload"/>.</param>
    public static string Encode(byte[] payload, byte[] signature) =>
        $"{Prefix}.{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(signature)}";

    /// <summary>
    /// Parses the token into its signature-covered payload bytes and its
    /// signature bytes. Anything that is not exactly the
    /// <see cref="Prefix"/> shape with two non-empty decodable segments
    /// refuses — the caller maps that to
    /// <see cref="LicenseValidationStatus.Malformed"/>.
    /// </summary>
    /// <param name="token">The pasted key text (surrounding whitespace tolerated).</param>
    /// <param name="payload">The payload bytes when the parse succeeds.</param>
    /// <param name="signature">The signature bytes when the parse succeeds.</param>
    public static bool TryParse(
        string? token,
        out byte[] payload,
        out byte[] signature)
    {
        payload = [];
        signature = [];

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var segments = token.Trim().Split('.');
        if (segments.Length != SegmentCount
            || segments[0] != Prefix
            || segments[1].Length == 0
            || segments[2].Length == 0)
        {
            return false;
        }

        try
        {
            payload = Base64Url.DecodeFromChars(segments[1]);
            signature = Base64Url.DecodeFromChars(segments[2]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
