namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// How much balance data one adapter source actually serves: the explicit
/// availability states exist so absence is never displayed as zero — a
/// zero would be a plausible-but-wrong figure the user could act on.
/// </summary>
public enum BalanceAvailability
{
    /// <summary>
    /// The adapter carries no trading face: it cannot query balances. The
    /// panel shows the explicit not-provided state instead of zeros.
    /// </summary>
    NotProvided = 0,

    /// <summary>
    /// The adapter served its balance dictionary (possibly empty before any
    /// fill — an empty served set is the adapter's honest answer).
    /// </summary>
    Provided = 1,

    /// <summary>
    /// The adapter has a trading face but the balance query failed. The
    /// fault is reported; zero is never substituted for a failed query.
    /// </summary>
    FetchFailed = 2,
}

/// <summary>
/// One adapter's balances for one snapshot: the served amounts or an
/// explicit unavailability. All states carry the source identity; only the
/// provided state carries live balances.
/// </summary>
public sealed record BalanceSource
{
    /// <summary>Adapter identifier (e.g. "paper", "binance").</summary>
    public required string AdapterId { get; init; }

    /// <summary>Adapter display name (e.g. "Paper Trading").</summary>
    public required string AdapterName { get; init; }

    /// <summary>What the adapter actually serves: provided, not provided or a failed query.</summary>
    public required BalanceAvailability Availability { get; init; }

    /// <summary>
    /// Served balances (asset code to amount). Empty for every state other
    /// than <see cref="BalanceAvailability.Provided"/> — the aggregate never
    /// reads this without checking the availability first.
    /// </summary>
    public required IReadOnlyDictionary<string, Money> Balances { get; init; }

    /// <summary>
    /// The fault detail of a failed query (empty otherwise: the detail is
    /// composed into a localized line only when <see
    /// cref="Availability"/> is <see cref="BalanceAvailability.FetchFailed"/>).
    /// </summary>
    public required string Failure { get; init; }

    /// <summary>Create the explicit not-provided entry for an adapter without a trading face.</summary>
    public static BalanceSource NotProvided(string adapterId, string adapterName) => new()
    {
        AdapterId = adapterId,
        AdapterName = adapterName,
        Availability = BalanceAvailability.NotProvided,
        Balances = new Dictionary<string, Money>(),
        Failure = string.Empty,
    };

    /// <summary>Create the provided entry from the balances an adapter actually served.</summary>
    public static BalanceSource Provided(
        string adapterId,
        string adapterName,
        IReadOnlyDictionary<string, Money> balances) => new()
        {
            AdapterId = adapterId,
            AdapterName = adapterName,
            Availability = BalanceAvailability.Provided,
            Balances = balances,
            Failure = string.Empty,
        };

    /// <summary>Create the failed-query entry; the fault detail rides along, never a zero.</summary>
    public static BalanceSource FetchFailed(
        string adapterId,
        string adapterName,
        string failure) => new()
        {
            AdapterId = adapterId,
            AdapterName = adapterName,
            Availability = BalanceAvailability.FetchFailed,
            Balances = new Dictionary<string, Money>(),
            Failure = failure,
        };
}
