namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Value conversions for the order history schema. Explicit (not provider
/// defaults) so the stored format stays stable across EF versions: enums as
/// readable strings, decimals and timestamps as invariant-culture TEXT
/// (SQLite has no native decimal/DateTimeOffset type).
/// </summary>
public static class OrderHistoryConversions
{
    #region Fields

    /// <summary>DateTimeOffset as roundtrip ISO text in UTC; sorts correctly as TEXT.</summary>
    public static readonly ValueConverter<DateTimeOffset, string> Timestamp =
        new(
            value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    /// <summary>
    /// Nullable variant — EF requires a converter built for the nullable type
    /// itself; nulls round-trip as SQL NULL outside the expression.
    /// </summary>
    public static readonly ValueConverter<DateTimeOffset?, string> NullableTimestamp =
        new(
            value => value.HasValue
                ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null!,
            value => string.IsNullOrEmpty(value)
                ? null
                : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    /// <summary>Decimal as exact invariant text (SQLite stores decimals as TEXT).</summary>
    public static readonly ValueConverter<decimal, string> Amount =
        new(
            value => value.ToString(CultureInfo.InvariantCulture),
            value => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture));

    /// <summary>Nullable variant of <see cref="Amount"/> (SQL NULL outside the expression).</summary>
    public static readonly ValueConverter<decimal?, string> NullableAmount =
        new(
            value => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : null!,
            value => string.IsNullOrEmpty(value)
                ? null
                : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture));

    #endregion Fields
}
