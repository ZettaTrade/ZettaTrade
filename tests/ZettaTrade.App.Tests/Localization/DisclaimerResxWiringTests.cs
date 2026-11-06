namespace ZettaTrade.App.Tests.Localization;

using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The disclaimer wording ships in BOTH cultures through the standard
/// ({loc:Localized Key}) catalog: every key the dialog and the Help menu
/// item bind resolves in en and ru, each carries real text (never the bare
/// key or a fallback) and the two languages carry different strings.
/// </summary>
public sealed class DisclaimerResxWiringTests
{
    /// <summary>Keys the disclaimer dialog and the Help menu item bind.</summary>
    private static readonly string[] DisclaimerKeys =
    {
        "Menu.Help",
        "Menu.Help.AboutDisclaimer",
        "Trading.Disclaimer.Title",
        "Trading.Disclaimer.NotAdvice",
        "Trading.Disclaimer.AlphaStatus",
        "Trading.Disclaimer.PaperFirst",
        "Trading.Disclaimer.RealFunds",
        "Trading.Disclaimer.KeyHygiene",
        "Trading.Disclaimer.WhereToRead",
        "Trading.Disclaimer.Acknowledge",
    };

    [Fact]
    public void EveryDisclaimerKey_ResolvesInEnAndRu_WithRealTranslatedText()
    {
        foreach (var key in DisclaimerKeys)
        {
            var en = Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("en"));
            var ru = Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru"));

            en.Should().NotBeNullOrWhiteSpace($"the en catalog must carry '{key}'");
            ru.Should().NotBeNullOrWhiteSpace($"the ru catalog must carry '{key}'");
            ru.Should().NotBe(en, $"the ru catalog must translate '{key}', not copy the en text");
        }
    }

    [Fact]
    public void TheRuCatalog_CarriesTheStandingStatements()
    {
        // The two statements the board task pins («не является инвестиционной
        // рекомендацией», «не использовать с реальными средствами без
        // собственного тестирования») must survive translations: both ru
        // paragraphs hold their claims.
        var notAdvice = Strings.ResourceManager.GetString(
            "Trading.Disclaimer.NotAdvice",
            CultureInfo.GetCultureInfo("ru"));
        var realFunds = Strings.ResourceManager.GetString(
            "Trading.Disclaimer.RealFunds",
            CultureInfo.GetCultureInfo("ru"));

        (notAdvice ?? string.Empty).Should().Contain("инвестиц", "the not-investment-advice statement is the point");
        (realFunds ?? string.Empty).Should().Contain("реальными средствами", "the own-testing-first statement is the point");
    }
}
