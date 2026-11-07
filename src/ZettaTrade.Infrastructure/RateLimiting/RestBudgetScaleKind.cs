namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The budget scale semantics of one adapter's manifest (T-106): how the
/// per-adapter budget settings key reshapes the endpoint buckets. Venues
/// publish different budget dialects — Binance chunks a weight-per-minute
/// allowance across endpoint families, Bybit caps each public endpoint with
/// a requests-per-second count — so the settings key reads the adapter's own
/// dialect (see <see cref="BudgetValueKey"/>).
/// </summary>
public enum RestBudgetScaleKind
{
    /// <summary>Weight tokens per minute: the settings value replaces the manifest's total budget; every bucket scales proportionally (Binance).</summary>
    WeightPerMinute,

    /// <summary>Requests per second: the settings value replaces each per-second bucket's budget directly (Bybit).</summary>
    RequestsPerSecond,
}

/// <summary>The settings-key suffix of one scale kind.</summary>
public static class RestBudgetScaleKindExtensions
{
    /// <summary>
    /// The per-adapter settings key suffix: <c>RequestWeightMinute</c> for a
    /// weight-per-minute manifest, <c>RequestsPerSecond</c> for a
    /// request-count manifest (avoiding two limiters reading each other's
    /// overrides).
    /// </summary>
    /// <param name="scaleKind">The manifest's scale kind.</param>
    /// <returns>The suffix after the adapter's settings key (e.g. <c>Limiter.Binance.RequestWeightMinute</c>).</returns>
    public static string BudgetValueKey(this RestBudgetScaleKind scaleKind) => scaleKind switch
    {
        RestBudgetScaleKind.WeightPerMinute => "RequestWeightMinute",
        RestBudgetScaleKind.RequestsPerSecond => "RequestsPerSecond",
        _ => throw new ArgumentOutOfRangeException(nameof(scaleKind)),
    };
}
