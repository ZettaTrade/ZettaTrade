namespace ZettaTrade.App.Services;

using System.Collections.Concurrent;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Round-size metadata of the paper trading surface — the only trading
/// lane the app ships today. The demo instrument set declares its lot
/// steps here the same way the paper fee model is declared per surface
/// (T-080): these are the paper surface's own simulation facts, not claims
/// about a real venue. Instruments outside the registry carry no
/// metadata, and the sizing helper keeps itself hidden for them.
/// </summary>
/// <remarks>
/// Real exchange-backed trading needs the adapters to surface their own
/// symbol filters (lot/step size and friends) through their adapter face;
/// until such a contract lands (a Core-gated addition, reported to the
/// supervisor), every non-paper instrument stays metadata-less and the
/// helper stays hidden for it. The lookup rides
/// <see cref="IInstrumentMetadataProvider"/> so the future adapter-backed
/// registry drops in without touching the ticket.
/// </remarks>
public sealed class PaperInstrumentMetadataProvider : IInstrumentMetadataProvider
{
    #region Constants

    /// <summary>Declared lot step of the paper BTC/USDT instrument.</summary>
    private const decimal BtcUsdtLotStep = 0.00001m;

    /// <summary>Declared lot step of the paper ETH/USDT instrument.</summary>
    private const decimal EthUsdtLotStep = 0.0001m;

    /// <summary>Declared lot step of the paper ETH/BTC instrument.</summary>
    private const decimal EthBtcLotStep = 0.00001m;

    /// <summary>Declared lot step of the paper SOL/USDT instrument.</summary>
    private const decimal SolUsdtLotStep = 0.01m;

    /// <summary>Declared lot step of the paper XRP/USDT instrument.</summary>
    private const decimal XrpUsdtLotStep = 0.01m;

    /// <summary>Bare capacity hint for the registry (the demo set plus headroom).</summary>
    private const int RegistryCapacity = 8;

    /// <summary>Registry concurrency level: single-digit capacity, single writer thread.</summary>
    private const int RegistryConcurrencyLevel = 4;

    #endregion Constants

    #region Private Fields

    /// <summary>
    /// The declared lot steps by normalized symbol. Keys are the canonical
    /// uppercase <see cref="Symbol.Normalized"/> forms; a venue never lists
    /// a symbol twice under different letter cases, so the exact form rules.
    /// </summary>
    private readonly ConcurrentDictionary<string, InstrumentSizeMetadata> _registry =
        new(RegistryConcurrencyLevel, RegistryCapacity, StringComparer.Ordinal);

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the provider and seed the registry with the paper surface's
    /// declared demo instruments (the set the demo market adapters list).
    /// </summary>
    public PaperInstrumentMetadataProvider()
    {
        Seed(Symbol.Parse("BTC/USDT"), BtcUsdtLotStep);
        Seed(Symbol.Parse("ETH/USDT"), EthUsdtLotStep);
        Seed(Symbol.Parse("ETH/BTC"), EthBtcLotStep);
        Seed(Symbol.Parse("SOL/USDT"), SolUsdtLotStep);
        Seed(Symbol.Parse("XRP/USDT"), XrpUsdtLotStep);
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc/>
    public InstrumentSizeMetadata? Find(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return _registry.TryGetValue(symbol.Normalized, out var metadata)
            ? metadata
            : null;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Register one instrument's declared lot step under its normalized name.</summary>
    private void Seed(Symbol symbol, decimal lotStep) =>
        _registry[symbol.Normalized] = new InstrumentSizeMetadata(symbol, lotStep);

    #endregion Private Methods
}
