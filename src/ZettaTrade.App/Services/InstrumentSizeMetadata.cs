namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The round-size facts the position sizing helper needs for one
/// instrument: the quantity step the venue accepts (the lot step that a
/// proposed size must land on). The helper never proposes a quantity
/// outside this grid, so a proposal can not carry more precision than the
/// instrument actually trades.
/// </summary>
public sealed record InstrumentSizeMetadata
{
    /// <summary>The instrument the metadata describes.</summary>
    public Symbol Symbol { get; }

    /// <summary>Positive lot step of the instrument.</summary>
    public decimal QuantityStep { get; }

    /// <summary>Create validated metadata: a lot step is always strictly positive.</summary>
    /// <exception cref="ArgumentNullException">When no instrument is named.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the step is not a positive number.</exception>
    public InstrumentSizeMetadata(Symbol symbol, decimal quantityStep)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (quantityStep <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityStep), quantityStep, "The quantity step must be positive.");
        }

        Symbol = symbol;
        QuantityStep = quantityStep;
    }
}
