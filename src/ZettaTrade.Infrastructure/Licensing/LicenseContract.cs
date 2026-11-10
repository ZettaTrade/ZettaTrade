namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The stable contract facts every license key obeys (T-132): the product
/// binding and the JSON options the payload serializes with. The issuer's
/// offline tooling and the app's validator must agree on these — the
/// options (camelCase, case-insensitive) are the cross-tool surface.
/// </summary>
public static class LicenseContract
{
    /// <summary>The product name every license key must bind to (a key for anything else refuses with <see cref="LicenseValidationStatus.WrongProduct"/>).</summary>
    public const string Product = "ZettaTrade";
}

/// <summary>The JSON serialization the license payload travels with (shared by the validator and the issuer tooling).</summary>
public static class LicenseJson
{
    /// <summary>
    /// The payload options: camelCase property names, case-insensitive
    /// reads — the issuer tooling is free to write the document with its
    /// own serializer as long as the field names agree.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);
}
