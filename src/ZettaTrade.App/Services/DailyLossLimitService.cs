namespace ZettaTrade.App.Services;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// One honest read of the daily loss governor's facts (T-189): the
/// feature's switch, whether the parsed symbol's quote bucket crossed the
/// threshold over the remaining window, and the numbers both surfaces —
/// the risk manager's rejection reason and the order ticket's banner —
/// print. <see cref="IsBlocked"/> is per quote bucket: an order is blocked
/// when ITS OWN quote currency's day loss crossed the limit, never because
/// another currency lost money today.
/// </summary>
/// <param name="Enabled">Whether the governor's master switch is on.</param>
/// <param name="IsBlocked">Whether the read symbol's quote bucket crossed the limit over the window. False while the switch is off, the threshold is nonsense, or the symbol carries no quote asset.</param>
/// <param name="DayLoss">The window's net realized sum for the symbol's quote bucket (negative = loss; zero when nothing was read).</param>
/// <param name="Limit">The configured threshold (positive).</param>
/// <param name="Quote">The symbol's quote currency (empty for a quoteless symbol).</param>
/// <param name="ExchangeId">The trading lane the facts belong to (the traded surface's own id).</param>
public sealed record DailyLossState(
    bool Enabled,
    bool IsBlocked,
    decimal DayLoss,
    decimal Limit,
    string Quote,
    string ExchangeId);

/// <summary>
/// The daily loss limit's state (T-189): the risk governor reads the day's
/// net realized result for the CURRENT trading lane and the order's own
/// quote bucket; the order ticket reads the same facts for its banner and
/// the operator's reset. The threshold, the master switch and the reset
/// marker ride the settings store (<c>Risk.DailyLossLimit*</c>) — re-read
/// at every decision, so a stored change applies at the next order without
/// a restart.
/// </summary>
/// <remarks>
/// Loss accounting rides the PnL engine's realized history: a row's stored
/// amount is already net of its commission (the ingest math nets them at
/// row-build time), so the day's loss is the plain window sum — never a
/// second commission subtraction. Buckets never sum across quote
/// currencies. The window is the local calendar day (its local midnight as
/// UTC bounds), tightened by the reset marker when the operator reset
/// mid-day; the end is the start plus exactly one day (a DST-shifted
/// boundary drifts by at most an hour and the next day's window re-anchors
/// at its own local midnight). The feature's off state reads the settings
/// only — no engine query at all, so an off governor changes nothing even
/// when the ledger is unreadable.
/// </remarks>
public sealed class DailyLossLimitService(
    ITradingAdapter trading,
    PnlAccountingService accounting,
    ISettingsService settings,
    TimeProvider? clock = null,
    ILogger<DailyLossLimitService>? logger = null) : IDailyLossLimit
{
    /// <summary>
    /// The clock the day window and the reset marker ride: <see
    /// cref="TimeProvider"/> is the testable seam (a fake provider moves
    /// the day without touching the wall clock); the shipped default is
    /// the system clock.
    /// </summary>
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly ILogger<DailyLossLimitService> _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DailyLossLimitService>.Instance;

    /// <summary>
    /// Read the governor's honest facts for one symbol on the current lane.
    /// Never throws for its own logic; an engine fault PROPAGATES — the
    /// risk manager turns it into a fail-closed rejection (BUG-026
    /// precedent), while the ticket's banner treats a fault as "unknown,
    /// stay hidden".
    /// </summary>
    public async Task<DailyLossState> ReadStateAsync(Symbol symbol, CancellationToken ct = default)
    {
        var enabled = await settings.GetAsync(
            RiskSettingsKeys.Section,
            RiskSettingsKeys.DailyLossLimitEnabled,
            RiskSettingsKeys.DailyLossLimitEnabledValue);
        var limit = await settings.GetAsync(
            RiskSettingsKeys.Section,
            RiskSettingsKeys.DailyLossLimitAmount,
            RiskSettingsKeys.DailyLossLimitAmountValue);
        var quote = symbol.QuoteAsset ?? string.Empty;
        var exchangeId = trading.Id;

        // Off (or a nonsense threshold — a non-positive limit can never be
        // "crossed" honestly): the governor changes nothing and performs no
        // engine read at all, so the off state stays bit-identical even on
        // an unreadable ledger. A nonsense threshold is an operator's
        // config fault, logged once per read while it stands.
        if (!enabled || limit <= 0m)
        {
            if (enabled && limit <= 0m)
            {
                _logger.LogWarning(
                    "Daily loss limit: the switch is on but the threshold {Limit} is not positive; the governor stays idle",
                    limit);
            }

            return new(enabled, false, 0m, limit, quote, exchangeId);
        }

        var marker = await ReadResetMarkerAsync(ct);
        var localNow = _clock.GetLocalNow();
        var dayStartUtc = new DateTimeOffset(
                localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset)
            .ToUniversalTime();
        var dayEndUtc = dayStartUtc + TimeSpan.FromDays(1);
        var windowStartUtc = marker is { } resetAt && resetAt > dayStartUtc ? resetAt : dayStartUtc;

        var buckets = await accounting.GetRealizedBucketsAsync(exchangeId, windowStartUtc, dayEndUtc, ct);
        var dayLoss = buckets.GetValueOrDefault(quote);

        // A quoteless order (a stock symbol carries no quote asset) cannot
        // be attributed honestly: the empty-string bucket may mix several
        // currencies, so the gate passes the order and logs the hazard —
        // never a fabricated currency, never a fabricated block.
        if (symbol.QuoteAsset is null)
        {
            _logger.LogWarning(
                "Daily loss limit: order symbol {Symbol} carries no quote asset; the loss gate passes it unattributed",
                symbol.Normalized);
        }

        var isBlocked = symbol.QuoteAsset is not null && dayLoss <= -limit;
        return new(enabled, isBlocked, dayLoss, limit, quote, exchangeId);
    }

    /// <summary>
    /// The operator's manual reset: write the reset marker at NOW. The
    /// block lifts for the rest of the day (the window's start tightens to
    /// the marker, dropping everything realized before it) and the next
    /// day's rollover re-anchors the window at its own local midnight.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var marker = _clock.GetUtcNow();
        await settings.SetAsync(
            RiskSettingsKeys.Section,
            RiskSettingsKeys.DailyLossResetAt,
            marker.ToString("O", CultureInfo.InvariantCulture));
        _logger.LogInformation(
            "Daily loss limit: the operator reset the governor; losses before {Marker} no longer block new orders today",
            marker);
    }

    /// <summary>
    /// Read the stored reset marker: absence (or unparsable text — logged,
    /// never substituted with a fabricated moment) is a null window marker.
    /// </summary>
    private async Task<DateTimeOffset?> ReadResetMarkerAsync(CancellationToken ct)
    {
        var raw = await settings.GetAsync<string?>(
            RiskSettingsKeys.Section,
            RiskSettingsKeys.DailyLossResetAt,
            null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var marker))
        {
            return marker;
        }

        _logger.LogWarning(
            "Daily loss limit: the stored reset marker {Raw} does not parse; the window ignores it",
            raw);
        return null;
    }
}
