namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Threading;
using System.Threading.Tasks;
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

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var configured = await _settings.GetAsync<BinanceEndpoints?>(
            SettingsSection,
            BinanceKey);

        if (configured is null)
        {
            // The config file simply has no Exchange section (the common
            // case): the shipped defaults win silently, hidden-safe.
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
}
