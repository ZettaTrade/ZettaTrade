namespace ZettaTrade.UI.Tests.Themes;

using Avalonia.Media;
using FluentAssertions;
using Livery.Themes;
using Xunit;

/// <summary>
/// The derived Fluent system-key facts (T-150): the stock control
/// templates' own families must ride the applied palette, so the second
/// parallel color system dies with them. The derivation is formula-pure —
/// the facts pin the exact bytes every ramp step produces and the complete
/// published key set. The publish semantics are all-or-nothing: a palette
/// missing or mistyping a foundation key derives nothing, matching how the
/// palette keys themselves persist when a theme file omits a role.
/// </summary>
public sealed class ThemeManagerDerivedKeysTests
{
    /// <summary>The complete derived family the publish is allowed to serve (26 keys).</summary>
    private static readonly string[] DerivedFamily =
    [
        "SystemAccentColor",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemControlForegroundBaseHighBrush",
        "SystemControlForegroundBaseMediumHighBrush",
        "SystemControlForegroundBaseMediumBrush",
        "SystemControlForegroundBaseMediumLowBrush",
        "SystemControlForegroundBaseLowBrush",
        "SystemControlDisabledBaseHighBrush",
        "SystemControlDisabledBaseMediumLowBrush",
        "SystemControlBackgroundBaseHighBrush",
        "SystemControlBackgroundBaseMediumHighBrush",
        "SystemControlBackgroundBaseMediumBrush",
        "SystemControlBackgroundBaseMediumLowBrush",
        "SystemControlBackgroundBaseLowBrush",
        "SystemControlBackgroundAltHighBrush",
        "SystemControlBackgroundChromeMediumLowBrush",
        "SystemControlBackgroundChromeMediumBrush",
        "SystemControlHighlightAccentBrush",
        "SystemControlHighlightAltAccentBrush",
        "SystemControlTransparentBrush",
        "SystemControlBackgroundTransparentBrush",
    ];

    private static readonly IReadOnlyDictionary<string, string> Palette = new Dictionary<string, string>
    {
        ["Background"] = "#101010",
        ["Surface"] = "#1a1a28",
        ["Border"] = "#202028",
        ["Accent"] = "#123456",
        ["TextPrimary"] = "#eeeeee",
    };

    [Fact]
    public void TryDerive_ServesTheCompleteFamily_FromThePalette()
    {
        var derived = FluentSystemKeys.TryDerive(Palette, out _);

        derived.Should().NotBeNull("a healthy palette derives the full family");
        derived!.Keys.Should().BeEquivalentTo(DerivedFamily,
            "the publish is exact: the family is complete and nothing else rides it");
        derived.Should().HaveCount(DerivedFamily.Length);
    }

    [Fact]
    public void TryDerive_TheAccentFamily_PinsTheExactRamp()
    {
        // Accent #123456: the identity member plus the darker and lighter
        // derivatives (blend toward White/Black, away-from-zero rounding):
        // Light1 = 18,52,86 toward #FFFFFF at 0.15 -> 54/82/111; Dark2 at
        // 0.30 -> 13/36/60.
        var derived = FluentSystemKeys.TryDerive(Palette, out _);

        derived.Should().NotBeNull();
        derived!["SystemAccentColor"].Should().Be(Color.Parse("#123456"));
        derived["SystemAccentColorLight1"].Should().Be(Color.Parse("#36526f"));
        derived["SystemAccentColorDark2"].Should().Be(Color.Parse("#0d243c"));
    }

    [Fact]
    public void TryDerive_TheSteps_PinTheExactBlends()
    {
        // The foreground ramp fades TextPrimary (#eeeeee) toward Background
        // (#101010): step 0.65 -> 238*.35 + 16*.65 = 93.7, rounded away
        // from zero -> 94 (#5E); the disabled step 0.55 -> 115.9 -> 116
        // (#74). The surface ramps blend Background toward Surface
        // (#1a1a28): AltHigh at 0.35 -> 19.5/19.5/24.4 -> #141418; the
        // chrome step 0.40 -> #14141a. The selection accents carry the
        // accent (Alt tints a step toward white: 0.30 -> 89/113/137 =
        // #597189), and the transparent pair carries no tint at all.
        var derived = FluentSystemKeys.TryDerive(Palette, out _);

        derived.Should().NotBeNull();
        derived!["SystemControlForegroundBaseMediumLowBrush"].Should().Be(Color.Parse("#5e5e5e"));
        derived["SystemControlDisabledBaseHighBrush"].Should().Be(Color.Parse("#747474"));
        derived["SystemControlBackgroundAltHighBrush"].Should().Be(Color.Parse("#141418"));
        derived["SystemControlBackgroundChromeMediumLowBrush"].Should().Be(Color.Parse("#14141a"));
        derived["SystemControlHighlightAccentBrush"].Should().Be(Color.Parse("#123456"));
        derived["SystemControlHighlightAltAccentBrush"].Should().Be(Color.Parse("#597189"));
        derived["SystemControlTransparentBrush"].Should().Be(Colors.Transparent);
        derived["SystemControlBackgroundTransparentBrush"].Should().Be(Colors.Transparent);
    }

    [Fact]
    public void TryDerive_WithAMissingFoundationKey_DerivesNothing()
    {
        var broken = new Dictionary<string, string>(Palette);
        broken.Remove("Surface");

        var derived = FluentSystemKeys.TryDerive(broken, out var failure);

        derived.Should().BeNull("an incoherent half-family would be a second parallel color system again");
        failure.Should().Contain("'Surface'");
    }

    [Fact]
    public void TryDerive_WithAnUnparsableFoundationColor_DerivesNothing()
    {
        var broken = new Dictionary<string, string>(Palette);
        broken["TextPrimary"] = "not-a-color";

        var derived = FluentSystemKeys.TryDerive(broken, out var failure);

        derived.Should().BeNull();
        failure.Should().Contain("'TextPrimary'").And.Contain("not a color");
    }
}
