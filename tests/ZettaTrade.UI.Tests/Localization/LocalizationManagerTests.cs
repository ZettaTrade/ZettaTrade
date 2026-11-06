namespace ZettaTrade.UI.Tests.Localization;

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

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
/// <remarks>
/// T-073 (the flaky parked-factory fact): the landing verification of the
/// T-071/T-072 merge failed this file's parked fact twice under parallel
/// suite load. The captured trace (actual 'ru:Menu.File') is scheduling,
/// not the manager: the fact's Task.Run caller can stay queued while the
/// pool carries the sibling suites, and an unrelated lookup that resolves
/// the same process-wide instance through production code (the order
/// book layout's Build path) already opened the former class-wide "any
/// lookup parked" witness. The switch then committed over a call that had
/// never reached its snapshot — the call read the fresh culture and
/// returned it, exactly the documented post-fix promise ("always the one
/// consistent with its own snapshot"): the manager was right, the
/// handshake was wrong. It now waits for THIS call's own thread id at a
/// per-name register, the static-resolving layout suite (T-073) joins the
/// collection, and Dispose stops a torn-down manager from keeping the
/// process-wide IsInitialized claim.
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
            // "en"; the factory parks before it can compute the value. The
            // call's own thread id is captured so the handshake can wait for
            // THIS call — never for a stranger's (the T-073 race story).
            var callerThreadId = -1;
            var parkedLookup = Task.Run(() =>
            {
                callerThreadId = Thread.CurrentThread.ManagedThreadId;
                return manager.GetString("Menu.File");
            });

            // Wait until THIS call actually parked, so the switch below is
            // guaranteed to cross the factory's execution, not the snapshot:
            // once this very thread registers, the culture it stamped with
            // is sealed and no later switch can reach it.
            var spin = new System.Threading.SpinWait();
            while (!resources.ParkedCalls.TryGetValue("Menu.File", out var parkedThread)
                || parkedThread != callerThreadId)
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

        /// <summary>
        /// The parked lookups by caller: the key is the resource name, the
        /// value the ManagedThreadId of the call that is parked on the gate
        /// right now. The deterministic fact waits for ITS OWN thread's
        /// registration — the former class-wide "any lookup has entered"
        /// flag could be satisfied by an unrelated lookup (the T-073
        /// interference), letting a switch commit over a call that had not
        /// even reached its snapshot.
        /// </summary>
        public ConcurrentDictionary<string, int> ParkedCalls { get; init; } = new();

        public override string? GetString(string name, CultureInfo? culture)
        {
            ParkUntilReleased(name);

            return $"{culture?.Name ?? "<null>"}:{name}";
        }

        /// <summary>
        /// While the gate is set and unresolved, every lookup parks —
        /// guaranteed bounded: the fact releases it in its finally. The
        /// caller's thread id stays registered as long as the park lasts,
        /// so a waiter can tell WHICH call is blocked.
        /// </summary>
        private void ParkUntilReleased(string name)
        {
            var park = Park;
            if (park is null)
            {
                return;
            }

            ParkedCalls[name] = Thread.CurrentThread.ManagedThreadId;
            var spin = new System.Threading.SpinWait();
            while (!park.Task.IsCompleted)
            {
                spin.SpinOnce();
            }

            ParkedCalls.TryRemove(name, out _);
        }
    }
}
