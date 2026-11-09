namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Subjects;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The performance HUD's test kit (T-108): a real ViewModel over a real
/// sampler, the book stream and the settings conversation driven by
/// subjects the test pushes into, and a localizer that echoes the
/// multi-argument format calls back as <c>[key:args]</c> — every label a
/// visible assertion instead of hidden plumbing.
/// </summary>
internal static class HudTests
{
    /// <summary>The kit's record: the HUD ViewModel plus every exposed collaborator.</summary>
    internal sealed record HudKit(
        PerformanceHudViewModel Hud,
        PerformanceHudService Service,
        ISettingsService Settings,
        Subject<SettingsChange> Changes,
        Subject<OrderBook> BookUpdates);

    /// <summary>
    /// A localizer for the HUD's format calls: multi-argument lookups echo
    /// back as <c>[key:arg1|arg2]</c>, so the lines' labels and their
    /// arguments read as assertions.
    /// </summary>
    internal static ILocalizationManager Localization()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization
            .GetString(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");

        return localization;
    }

    /// <summary>
    /// A fully wired HUD kit: the persisted choice answers
    /// <paramref name="persistedVisibility"/>, the changes stream and the
    /// book stream are open subjects, the backpressure probe answers
    /// <paramref name="backpressureBacklog"/> of the
    /// <see cref="PerformanceHudService"/>'s 8192 gauge.
    /// </summary>
    internal static HudKit CreateKit(
        bool persistedVisibility = UiSettingsKeys.DefaultHudEnabled,
        int backpressureBacklog = 0)
    {
        var (settings, changes) = ChatTests.VisibilitySettings(persistedVisibility);
        var bookUpdates = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            bookUpdates,
            () => backpressureBacklog,
            8192,
            NullLogger<PerformanceHudService>.Instance);
        var hud = new PerformanceHudViewModel(
            settings,
            Localization(),
            service,
            NullLogger<PerformanceHudViewModel>.Instance);

        return new HudKit(hud, service, settings, changes, bookUpdates);
    }

    /// <summary>An anonymous HUD off the kit (the menu tests' collaborator).</summary>
    internal static PerformanceHudViewModel CreateAnonymousHud() => CreateKit().Hud;

    /// <summary>A book snapshot stamped a moment ago (a fresh delivery).</summary>
    internal static OrderBook FreshBook() => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Bids = [],
        Asks = [],
        Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>A book snapshot stamped in the near future: its age clamps below zero, so the histogram reads its bucket deterministically (no race against a slow runner).</summary>
    internal static OrderBook FutureBook() => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Bids = [],
        Asks = [],
        Timestamp = DateTimeOffset.UtcNow.AddSeconds(10),
    };

    /// <summary>A book snapshot stamped five minutes ago — the histogram's overflow bucket deterministically.</summary>
    internal static OrderBook StaleBook() => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Bids = [],
        Asks = [],
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
    };
}
