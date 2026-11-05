namespace ZettaTrade.UI.Tests.Localization;

using System;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Livery.Localization;

/// <summary>
/// The localization lookup under a live language switch (BUG-051): the
/// per-call culture snapshot binds the cache key and the resource lookup
/// together, so no mixture of one culture's key with another culture's
/// value can ever land in (or out of) the cache — during a switching
/// storm, after it, and in the deterministic mid-flight factory case,
/// where the parked call must return the string of the culture IT
/// snapshotted, not whatever culture happens to be current once the
/// factory resumes.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection: every manager instance
/// registers itself on <see cref="LocalizationManager"/>.Instance, and
/// <see cref="ZettaTrade.UI.Charting.ChartTickCulture.Resolve"/> resolves
/// through that static — two manager-creating classes must not run
/// concurrently, or one reads the other's just-switched language.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class LocalizationManagerTests
{
    private static LocalizationManager CreateManager(StampingResourceManager resources) =>
        new(Substitute.For<ILogger>(), resources);

    /// <summary>
    /// The culture the manager rewrites while switching belongs to the
    /// calling thread; leave the test thread exactly as it found it.
    /// </summary>
    private static (CultureInfo UICulture, CultureInfo Culture) CaptureAmbient() =>
        (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

    private static void RestoreAmbient((CultureInfo UICulture, CultureInfo Culture) before)
    {
        CultureInfo.CurrentUICulture = before.UICulture;
        CultureInfo.CurrentCulture = before.Culture;
    }

    /// <summary>
    /// The stamping glue: every fresh lookup carries the culture of the key
    /// it was cached under, before and after a switch.
    /// </summary>
    [Fact]
    public async Task GetString_TheValueCarriesTheKeyCulture_BeforeAndAfterASwitch()
    {
        var ambient = CaptureAmbient();
        var manager = CreateManager(new StampingResourceManager());

        try
        {
            await manager.SetLanguageAsync("en");

            manager.GetString("Menu.File").Should().Be(
                "en:Menu.File",
                "the value is the stamp of the culture the lookup was made under");

            await manager.SetLanguageAsync("ru");

            manager.GetString("Menu.File").Should().Be(
                "ru:Menu.File",
                "the switch re-reads under the new culture");
        }
        finally
        {
            RestoreAmbient(ambient);
            manager.Dispose();
        }
    }

    /// <summary>
    /// The deterministic BUG-051 case: a lookup parks inside its resource
    /// factory while a language switch commits. The parked call holds ITS
    /// snapshot — it must return the snapshot culture's string, never the
    /// currency the manager carries when the factory resumes (the pre-fix
    /// factory re-read the field and stamped the NEW culture's value under
    /// the OLD culture's cache key — a wrong-culture string handed to a
    /// caller holding the old snapshot). The fresh post-switch lookups stay
    /// clean: the junk entry the parked call repopulates is keyed on the
    /// snapshot culture and is never served to a new-culture lookup.
    /// </summary>
    [Fact]
    public async Task GetString_AFactoryParkedAcrossASwitch_ReturnsTheSnapshotCultureString()
    {
        var ambient = CaptureAmbient();
        var resources = new StampingResourceManager
        {
            Park = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var manager = CreateManager(resources);

        try
        {
            await manager.SetLanguageAsync("en");

            // The racing call: its snapshot (and therefore its cache key) is
            // "en"; the factory parks before it can compute the value.
            var parkedLookup = Task.Run(() => manager.GetString("Menu.File"));

            // Wait until the factory actually parked, so the switch below is
            // guaranteed to cross the factory's execution, not the snapshot.
            var spin = new System.Threading.SpinWait();
            while (!resources.ParkEntered)
            {
                spin.SpinOnce();
            }

            // The switch commits while the factory is parked.
            await manager.SetLanguageAsync("ru");

            resources.Park!.TrySetResult();

            var parkedValue = await parkedLookup;
            parkedValue.Should().Be(
                "en:Menu.File",
                "the parked call is bound to the culture IT snapshotted, not the current one (actual '{0}', current '{1}')",
                parkedValue,
                manager.CurrentLanguage);

            manager.CurrentLanguage.Should().Be("ru");
            manager.GetString("Menu.File").Should().Be(
                "ru:Menu.File",
                "the post-switch lookups never see the parked call's entry");
        }
        finally
        {
            resources.Park?.TrySetResult();
            RestoreAmbient(ambient);
            manager.Dispose();
        }
    }

    /// <summary>
    /// The switching storm (BUG-051, scale case): many concurrent readers
    /// against rapid language switches must settle on a cache that serves
    /// the CURRENT culture for every key — no mixed-culture entry persisted
    /// by a racing factory. Under the pre-fix two-level read race a storm
    /// could leave poison behind until the next switch; the per-call
    /// snapshot makes every cached entry self-consistent by construction.
    /// </summary>
    [Fact]
    public async Task GetString_SwitchingStorm_NeverLeavesAMixedCultureEntryBehind()
    {
        var ambient = CaptureAmbient();
        var manager = CreateManager(new StampingResourceManager());

        try
        {
            var readerLoops = Enumerable.Range(0, 4).Select(loop => Task.Run(() =>
            {
                for (var iteration = 0; iteration < 150; iteration++)
                {
                    // Consume the return: the per-call invariant is checked
                    // by the post-storm sweep (a mid-storm caller may see
                    // either language's self-consistent string).
                    var _ = manager.GetString(loop % 2 == 0 ? "Menu.File" : "Order.Buy");
                }
            })).ToArray();

            var switchLoop = Task.Run(async () =>
            {
                for (var swap = 0; swap < 24; swap++)
                {
                    await manager.SetLanguageAsync(swap % 2 == 0 ? "ru" : "en");
                }
            });

            await Task.WhenAll(readerLoops.Append(switchLoop).ToArray());

            manager.CurrentLanguage.Should().Be("en", "the storm ends on an even swap count");
            manager.GetString("Menu.File").Should().Be(
                "en:Menu.File",
                "the final culture serves its own string — no parked factory left poison");
            manager.GetString("Order.Buy").Should().Be(
                "en:Order.Buy",
                "every key resolves clean after the storm");
        }
        finally
        {
            RestoreAmbient(ambient);
            manager.Dispose();
        }
    }

    /// <summary>
    /// The resource manager the facts point at: every lookup returns the
    /// LOOKUP culture's name stamped onto the key, making any key/value
    /// culture mixture directly observable, with an optional park hook for
    /// the deterministic mid-flight fact (BUG-051). The spin is bounded by
    /// the fact releasing the gate in a finally.
    /// </summary>
    private sealed class StampingResourceManager : ResourceManager
    {
        /// <summary>While set and unresolved, every lookup parks before returning.</summary>
        public TaskCompletionSource? Park { get; init; }

        /// <summary>True once any lookup has run — the deterministic fact waits for this.</summary>
        public volatile bool ParkEntered;

        public override string? GetString(string name, CultureInfo? culture)
        {
            ParkUntilReleased();

            return $"{culture?.Name ?? "<null>"}:{name}";
        }

        /// <summary>
        /// While the gate is set and unresolved, every lookup parks —
        /// guaranteed bounded: the fact releases it in its finally.
        /// </summary>
        private void ParkUntilReleased()
        {
            var park = Park;
            if (park is null)
            {
                return;
            }

            ParkEntered = true;
            var spin = new System.Threading.SpinWait();
            while (!park.Task.IsCompleted)
            {
                spin.SpinOnce();
            }
        }
    }
}
