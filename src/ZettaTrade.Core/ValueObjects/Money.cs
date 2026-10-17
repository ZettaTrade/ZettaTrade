namespace ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a monetary amount with currency.
/// Immutable value object with precise decimal arithmetic.
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

    public override string ToString() => $"{Amount:F8} {Currency}";
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

    public decimal ChangePercent(Price from) =>
        from.Value == 0 ? 0 : (Value - from.Value) / from.Value * 100m;

    public override string ToString() => Value.ToString("F8");
}

/// <summary>
/// Represents a quantity (amount of asset).
/// </summary>
public readonly record struct Quantity(decimal Value)
{
    public static Quantity Zero => new(0m);

    public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value);
    public static Quantity operator -(Quantity a, Quantity b) => new(a.Value - b.Value);

    public Money NotionalAt(Price price) => new(Value * price.Value, string.Empty);

    public override string ToString() => Value.ToString("F8");
}
