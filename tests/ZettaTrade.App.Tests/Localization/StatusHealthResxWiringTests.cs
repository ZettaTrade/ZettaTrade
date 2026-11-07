namespace ZettaTrade.App.Tests.Localization;

using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The adapter health strip's wording (T-098) ships in BOTH cultures
/// through the standard ({loc:Localized}) catalog: the two dedicated state
/// labels translate, and both tooltip templates carry exactly the slots
/// the compositions feed — four for the supervised prose (name, state
/// label, last event clock, consecutive failures), two for the
/// unsupervised one (name, state label).
/// </summary>
public sealed class StatusHealthResxWiringTests
{
    /// <summary>The strip's own labels — the keys with no connection-chip twin.</summary>
    private static readonly string[] HealthStateKeys =
    {
        "Status.Health.Reconnecting",
        "Status.Health.Unsupervised",
    };

    [Fact]
    public void EveryHealthStateKey_ResolvesInEnAndRu_WithRealTranslatedText()
    {
        foreach (var key in HealthStateKeys)
        {
            var en = Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("en"));
            var ru = Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru"));

            en.Should().NotBeNullOrWhiteSpace($"the en catalog must carry '{key}'");
            ru.Should().NotBeNullOrWhiteSpace($"the ru catalog must carry '{key}'");
            ru.Should().NotBe(en, $"the ru catalog must translate '{key}', not copy the en text");
        }
    }

    /// <summary>The supervised template's slot feeds (the composition's argument order).</summary>
    private static readonly string[] SupervisedSlots = { "{0}", "{1}", "{2}", "{3}" };

    /// <summary>The unsupervised template's slot feeds.</summary>
    private static readonly string[] UnsupervisedSlots = { "{0}", "{1}" };

    [Fact]
    public void TheTooltipTemplates_CarryExactlyTheCompositionSlots_InBothCultures()
    {
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("ru") })
        {
            var supervised = Strings.ResourceManager.GetString("Status.Health.Tooltip", culture);
            supervised.Should().NotBeNullOrWhiteSpace($"the {culture.Name} catalog must carry the supervised template");
            foreach (var slot in SupervisedSlots)
            {
                supervised.Should().Contain(slot, $"the {culture.Name} supervised tooltip feeds the {slot} slot");
            }
            supervised.Should().NotContain(
                "{4}",
                "the supervised tooltip composes exactly four slots");

            var unsupervised = Strings.ResourceManager.GetString("Status.Health.TooltipUnsupervised", culture);
            unsupervised.Should().NotBeNullOrWhiteSpace($"the {culture.Name} catalog must carry the unsupervised template");
            foreach (var slot in UnsupervisedSlots)
            {
                unsupervised.Should().Contain(slot, $"the {culture.Name} unsupervised tooltip feeds the {slot} slot");
            }
            unsupervised.Should().NotContain(
                "{2}",
                "the unsupervised adapter has no event moment — the template never promises one");
        }
    }

    /// <summary>The resx keys the strip reuses from the connection chip still resolve in ru (the tooltip's state labels).</summary>
    [Fact]
    public void TheSharedConnectionKeys_TheStripReuses_ResolveInRu()
    {
        var ru = Strings.ResourceManager.GetString("Status.Connected", CultureInfo.GetCultureInfo("ru"));
        var disconnected = Strings.ResourceManager.GetString("Status.Disconnected", CultureInfo.GetCultureInfo("ru"));

        ru.Should().StartWith("Подключ", "the connected label rides the connection chip's existing key");
        disconnected.Should().StartWith("Отключ", "the down label rides the connection chip's existing key");
    }
}
