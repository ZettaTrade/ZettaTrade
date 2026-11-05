namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Globalization;
using System.Resources;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.Appearance.Localization;
using ZettaTrade.Appearance.Resources;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's label-culture resolution: the culture follows the
/// applied UI language of the localization manager LIVE, because the chart
/// draws on Avalonia's render thread where the ambient
/// <c>CultureInfo.CurrentCulture</c> still holds the process default (the
/// language switch rewrites only the UI thread's culture) — an ambient read
/// there was the mixed-language axis defect.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection: this fact resolves
/// through <see cref="LocalizationManager"/>.Instance (the static), so any
/// other manager-creating fact running concurrently would resolve the
/// wrong instance's just-switched language.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class ChartTickCultureTests
{
    /// <summary>
    /// Resolving against a freshly wired manager returns the applied
    /// language's culture and tracks a language switch, so a redraw after
    /// the switch re-inks the axis without a restart.
    /// </summary>
    [Fact]
    public async Task Resolve_TheManagerLanguage_TracksItLive()
    {
        var uiCultureBefore = CultureInfo.CurrentUICulture;
        var cultureBefore = CultureInfo.CurrentCulture;
        var manager = new LocalizationManager(
            Substitute.For<ILogger>(),
            Strings.ResourceManager);

        try
        {
            await manager.SetLanguageAsync("en");

            ChartTickCulture.Resolve().Name.Should().Be("en");

            await manager.SetLanguageAsync("ru");

            ChartTickCulture.Resolve().Name.Should().Be(
                "ru",
                "the culture re-resolves per redraw, so the language switch re-inks the axis");
        }
        finally
        {
            // SetLanguageAsync rewrites the CURRENT thread's ambient cultures;
            // leave the test thread exactly as it found it.
            CultureInfo.CurrentUICulture = uiCultureBefore;
            CultureInfo.CurrentCulture = cultureBefore;
            manager.Dispose();
        }
    }
}
