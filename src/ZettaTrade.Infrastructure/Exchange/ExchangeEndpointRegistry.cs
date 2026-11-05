namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// App-lifetime snapshot of the exchange transport endpoints, read ONCE
/// from the settings file: the configuration piece of the adapter registry
/// seam (the site T-016's selector will later enumerate).
/// </summary>
/// <remarks>
/// <c>ISettingsService</c> is async-only while DI construction is
/// synchronous — so the settings read happens in
/// <see cref="InitializeAsync"/>. The composition-root init loop initializes
/// implementations in registration order, and the registration site puts
/// this registry BEFORE every adapter: by the time an adapter resolves the
/// cached <see cref="Binance"/> value, the settings file is fully loaded.
/// Endpoint changes apply only after an app restart.
/// </remarks>
public sealed class ExchangeEndpointRegistry : IAsyncInitializable
{
    /// <summary>Settings section of the exchange transport configuration.</summary>
    public const string SettingsSection = "Exchange";

    /// <summary>Settings key carrying the Binance endpoint record.</summary>
    public const string BinanceKey = "Binance";

    /// <summary>Settings key carrying the Bybit endpoint record.</summary>
    public const string BybitKey = "Bybit";

    private readonly ILogger<ExchangeEndpointRegistry> _logger;
    private readonly ISettingsService _settings;

    /// <summary>
    /// Create a registry with the shipped production endpoints; an
    /// initialized settings override replaces them.
    /// </summary>
    public ExchangeEndpointRegistry(
        ISettingsService settings,
        ILogger<ExchangeEndpointRegistry> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Binance adapter endpoints: shipped production defaults until an
    /// initialized settings override (which then replaces the value for
    /// every adapter constructed afterwards).
    /// </summary>
    public BinanceEndpoints Binance { get; private set; } = BinanceEndpoints.Production;

    /// <summary>
    /// Bybit adapter endpoints: shipped production defaults until an
    /// initialized settings override (same semantics as <see cref="Binance"/>).
    /// </summary>
    public BybitEndpoints Bybit { get; private set; } = BybitEndpoints.Production;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ReadBinanceEndpoints(await _settings.GetAsync<BinanceEndpoints?>(SettingsSection, BinanceKey));
        ReadBybitEndpoints(await _settings.GetAsync<BybitEndpoints?>(SettingsSection, BybitKey));
    }

    /// <summary>
    /// Apply the Binance override when present: a missing entry keeps the
    /// shipped defaults silently (the common case — the config file simply
    /// has no Exchange section); an incomplete override (blank members) is a
    /// hidden-safe warning plus the shipped defaults; a complete override
    /// replaces the value.
    /// </summary>
    private void ReadBinanceEndpoints(BinanceEndpoints? configured)
    {
        if (configured is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configured.RestBaseUrl) ||
            string.IsNullOrWhiteSpace(configured.WsUrl))
        {
            _logger.LogWarning(
                "Exchange:{Key} endpoint override is incomplete; the shipped production endpoints stay in use",
                BinanceKey);
            return;
        }

        Binance = configured;
        _logger.LogInformation(
            "Exchange endpoints configured: rest {RestBaseUrl}, ws {WsUrl}",
            configured.RestBaseUrl,
            configured.WsUrl);
    }

    /// <summary>
    /// Apply the Bybit override under the same semantics as the Binance one.
    /// </summary>
    private void ReadBybitEndpoints(BybitEndpoints? configured)
    {
        if (configured is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configured.RestBaseUrl) ||
            string.IsNullOrWhiteSpace(configured.WsUrl))
        {
            _logger.LogWarning(
                "Exchange:{Key} endpoint override is incomplete; the shipped production endpoints stay in use",
                BybitKey);
            return;
        }

        Bybit = configured;
        _logger.LogInformation(
            "Exchange endpoints configured: rest {RestBaseUrl}, ws {WsUrl}",
            configured.RestBaseUrl,
            configured.WsUrl);
    }
}

