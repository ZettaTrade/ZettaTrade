namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The full REST rate-limit configuration of one adapter (T-106): the
/// endpoint families it shapes, the user/background partition, the budget
/// scale dialect and the adapter's settings address. The shipped instance
/// lives on the adapter as a static default (see
/// <c>BinanceAdapter.DefaultRestLimits</c> / <c>BybitAdapter.DefaultRestLimits</c>),
/// built from the adapters' real endpoint families; the reactive
/// <c>Limiter.*</c> settings keys reshape it live, so a settings edit never
/// needs a restart.
/// </summary>
/// <remarks>
/// The settings keys the limiter reads through the reactive settings
/// stream (all inside the <see cref="SettingsSection"/> section; live
/// edits apply without restart, absent keys fall back to the shipped
/// defaults; the storage round-trips ints as longs/doubles — the limiter
/// accepts both):
/// <list type="bullet">
///   <item><c>Limiter.Enabled</c> (bool, default true) — the global off
///   switch; when off, every lane passes unshaped (the demo/testnet
///   posture is structural exemption; this is the operator's runtime
///   switch for the same effect).</item>
///   <item><c>Limiter.BackgroundShare</c> (double, default
///   <see cref="DefaultBackgroundShare"/>, clamped to [0, 0.9]) — the
///   background slack cap and its slice of the refill flow.</item>
///   <item><c>Limiter.<see cref="SettingsKey"/>.<see cref="RestBudgetScaleKindExtensions.BudgetValueKey"/></c>
///   (number) — the per-adapter budget value: for
///   <see cref="RestBudgetScaleKind.WeightPerMinute"/> the total
///   per-minute allowance the buckets scale proportionally from
///   (Binance's <c>RequestWeightMinute</c>); for
///   <see cref="RestBudgetScaleKind.RequestsPerSecond"/> the per-second
///   count each bucket re-bases itself to (Bybit's
///   <c>RequestsPerSecond</c>). A non-positive value is ignored with a
///   logged warning, never a silently-zeroed limiter.</item>
/// </list>
/// </remarks>
public sealed record RestRateLimiterOptions
{
    /// <summary>
    /// The limiter settings section every adapter's limiter keys live in
    /// (e.g. <c>Limiter.Binance.RequestWeightMinute</c>).
    /// </summary>
    public const string SettingsSection = "Limiter";

    /// <summary>
    /// The default share of every endpoint's budget and refill flow that the
    /// background lane may draw (the slack cap and the slack's slice of the
    /// venue flow). The user lane keeps the rest — a user submit therefore
    /// never queues behind a backfill burst.
    /// </summary>
    public const double DefaultBackgroundShare = 0.35;

    /// <summary>
    /// The adapter's settings address (e.g. <c>Binance</c>): the prefix of
    /// that adapter's budget key inside the limiter section, so two limiters
    /// can never read each other's overrides.
    /// </summary>
    public required string SettingsKey { get; init; }

    /// <summary>
    /// The budget scale dialect of the manifest: how the per-adapter budget
    /// settings value reshapes the endpoint buckets (see
    /// <see cref="RestBudgetScaleKind"/> and the defaults' XML docs on the
    /// adapters).
    /// </summary>
    public required RestBudgetScaleKind ScaleKind { get; init; }

    /// <summary>
    /// The maximum share of every endpoint's budget the background lane may
    /// draw as shipped (the user lane keeps the complement): the acceptance
    /// — a user submit never starves behind a backfill — is pinned by this
    /// partition, adjustable live but never openable past the share ceiling.
    /// </summary>
    public double BackgroundShare { get; init; } = DefaultBackgroundShare;

    /// <summary>
    /// The endpoint families the adapter shapes, in the adapter's own token
    /// dialect (weight-per-minute or requests-per-second; keyed by the
    /// adapter's path-template constants). Every outbound REST call of the
    /// adapter must name one of these endpoints: an unmanifested call is a
    /// configuration error, never a silently throttled or throttled-by-
    /// default request.
    /// </summary>
    public required IReadOnlyList<RestEndpointBudget> Endpoints { get; init; }
}
