namespace ZettaTrade.Core.ValueObjects;

using System.Globalization;

/// <summary>
/// Represents a monetary amount with currency.
/// Immutable value object with precise decimal arithmetic.
/// All string operations use InvariantCulture for consistency.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Cannot add {a.Currency} and {b.Currency}.");
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Cannot subtract {b.Currency} from {a.Currency}.");
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator *(Money m, decimal factor) => new(m.Amount * factor, m.Currency);

    public bool IsPositive => Amount > 0;
    public bool IsNegative => Amount < 0;
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
    public static Price Zero => new(0m);

    public static Price operator +(Price a, Price b) => new(a.Value + b.Value);
    public static Price operator -(Price a, Price b) => new(a.Value - b.Value);
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

    public decimal ChangePercent(Price from) => 
        from.Value == 0 ? 0 : (Value - from.Value) / from.Value * 100m;

    public override string ToString() => Value.ToString("F8", CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents a quantity (amount of asset).
/// </summary>
public readonly record struct Quantity(decimal Value)
{
    public static Quantity Zero => new(0m);

    public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value);
    public static Quantity operator -(Quantity a, Quantity b) => new(a.Value - b.Value);

    /// <summary>
    /// Calculate notional value at given price.
    /// Currency must be provided since Quantity doesn't store it.
    /// </summary>
    public Money NotionalAt(Price price, string currency) => new(Value * price.Value, currency);

    public override string ToString() => Value.ToString("F8", CultureInfo.InvariantCulture);
}
