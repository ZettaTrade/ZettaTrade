namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Resources;

/// <summary>
/// The shared fixtures of the T-041 sink's app-side facts (the composer's
/// goldens, the bridge's dispatch facts and the e2e ride): a localization
/// catalog fed by the real shipped resx, an in-memory reactive settings
/// store, a recording logger and the journaled order row the publish
/// surface reads.
/// </summary>
internal static class TelegramSinkFixtures
{
    #region Constants

    /// <summary>The chat id one configured sink sends to.</summary>
    internal const string ChatId = "424242";

    #endregion Constants

    #region The journaled order row

    /// <summary>A journaled order row whose terminal facts the outcome surface reads.</summary>
    internal static Order BuildOrder(
        OrderStatus status,
        decimal filled,
        string exchangeId = "paper",
        decimal price = 50_000m,
        string rejectReason = "") => new()
        {
            Id = "paper-1",
            ExchangeId = exchangeId,
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.02m),
            Price = new Price(price),
            FilledQuantity = new Quantity(filled),
            RejectReason = rejectReason.Length == 0 ? null : rejectReason,
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
        };

    #endregion

    /// <summary>
    /// A localization catalog speaking the app's shipped resx lines: the
    /// templates resolve through the resource manager under the seeded
    /// culture, so a golden pins the catalog's own words (never a test's
    /// invented string). The static-singleton LocalizationManager is never
    /// constructed here — the tests read the same resource set directly.
    /// </summary>
    internal sealed class ResxCatalogLocalization : ILocalizationManager
    {
        private readonly CultureInfo _culture;

        internal ResxCatalogLocalization(string cultureCode) =>
            _culture = CultureInfo.GetCultureInfo(cultureCode);

        public string CurrentLanguage => _culture.Name;

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key) =>
            Strings.ResourceManager.GetString(key, _culture)
            ?? throw new InvalidOperationException($"The catalog holds no line for {key}.");

        public string GetString(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, GetString(key), args);

        public IObservable<string> LanguageChanged { get; } = Observable.Never<string>();

        public IObservable<string> GetObservable(string key) => Observable.Never<string>();
    }

    /// <summary>
    /// An in-memory reactive settings double: the persisted reads answer
    /// the store (typed), the writes land in it — the sink's per-send reads
    /// see exactly what a test stored.
    /// </summary>
    internal sealed class TelegramTestSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

        public IObservable<SettingsChange> Changes => Observable.Never<SettingsChange>();

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            if (_store.TryGetValue($"{section}\u0001{key}", out var value) && value is T typed)
            {
                return Task.FromResult(typed);
            }

            return Task.FromResult(defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            _store[$"{section}\u0001{key}"] = value;
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A recording logger: the facts read the level + message + exception triples a service logged.</summary>
    internal sealed class SpyLogger<TCategory> : ILogger<TCategory>
    {
        internal List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
