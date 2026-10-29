namespace ZettaTrade.Core.ValueObjects;

using System.Globalization;

/// <summary>
/// Represents a monetary amount with currency.
/// Immutable value object with precise decimal arithmetic.
/// All string operations use InvariantCulture for consistency.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>Zero money value for the specified currency.</summary>
    public static Money Zero(string currency) => new(0m, currency);

    /// <summary>Add two money values (must have same currency).</summary>
    public static Money operator +(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Cannot add {a.Currency} and {b.Currency}.");
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    /// <summary>Subtract two money values (must have same currency).</summary>
    public static Money operator -(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Cannot subtract {b.Currency} from {a.Currency}.");
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    /// <summary>Multiply money by a scalar factor.</summary>
    public static Money operator *(Money m, decimal factor) => new(m.Amount * factor, m.Currency);

    /// <summary>True if amount is positive.</summary>
    public bool IsPositive => Amount > 0;

    /// <summary>True if amount is negative.</summary>
    public bool IsNegative => Amount < 0;

    /// <summary>True if amount is zero.</summary>
    public bool IsZero => Amount == 0;

    /// <summary>
    /// Format with InvariantCulture to avoid culture-dependent output.
    /// </summary>
    public override string ToString() => $"{Amount.ToString("F8", CultureInfo.InvariantCulture)} {Currency}";

    /// <summary>
    /// Format with specific number of decimal places.
    /// </summary>
    public string ToString(int decimals) => $"{Amount.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {Currency}";
}

/// <summary>
/// Represents a price value.
/// </summary>
public readonly record struct Price(decimal Value)
{
    /// <summary>Zero price value.</summary>
    public static Price Zero => new(0m);

    /// <summary>Add two prices.</summary>
    public static Price operator +(Price a, Price b) => new(a.Value + b.Value);

    /// <summary>Subtract two prices.</summary>
    public static Price operator -(Price a, Price b) => new(a.Value - b.Value);

    /// <summary>Multiply price by a scalar factor.</summary>
    public static Price operator *(Price p, decimal factor) => new(p.Value * factor);

    /// <summary>
    /// Multiply price by quantity to get money value.
    /// Currency must be provided since Price doesn't store it.
    /// </summary>
    public static Money operator *(Price price, Quantity quantity) => new(price.Value * quantity.Value, string.Empty);

    /// <summary>
    /// Multiply price by quantity with explicit currency.
    /// </summary>
    public Money Multiply(Quantity quantity, string currency) => new(Value * quantity.Value, currency);

    /// <summary>
    /// Calculate percentage change from another price.
    /// </summary>
    public decimal ChangePercent(Price from) =>
        from.Value == 0 ? 0 : (Value - from.Value) / from.Value * 100m;

    public override string ToString() => Value.ToString("F8", CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents a quantity (amount of asset).
/// </summary>
public readonly record struct Quantity(decimal Value)
{
    /// <summary>Zero quantity value.</summary>
    public static Quantity Zero => new(0m);

    /// <summary>Add two quantities.</summary>
    public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value);

    /// <summary>Subtract two quantities.</summary>
    public static Quantity operator -(Quantity a, Quantity b) => new(a.Value - b.Value);

    /// <summary>
    /// Calculate notional value at given price.
    /// Currency must be provided since Quantity doesn't store it.
    /// </summary>
    public Money NotionalAt(Price price, string currency) => new(Value * price.Value, currency);

    public override string ToString() => Value.ToString("F8", CultureInfo.InvariantCulture);
}
