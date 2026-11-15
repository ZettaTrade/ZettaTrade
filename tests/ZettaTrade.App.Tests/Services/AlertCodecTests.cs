namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The alerts codec's persistence facts (T-096, T-204): a full roster round-trips
/// every fact (both kinds, optional stamps survive, the re-arm facts ride
/// version 3), each stored entry is
/// exact-or-skipped (unknown kind/garbage enums/missing required facts skip
/// with a count — no default-substituted rows), the percent kind's unused
/// price facts are ignored harmlessly, stored member names and enums parse
/// case-insensitively, and a wholly unreadable root is a thrown,
/// caller-visible fault instead of a fabricated roster.
/// </summary>
public sealed class AlertCodecTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The base's ticks the stamped members carry (the codec's stamps are DateTimeOffset ticks).</summary>
    private static readonly string BaseTicksJson = Base.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static AlertDefinition PriceAlert(
        AlertStatus status = AlertStatus.Armed,
        string note = "seller fatigue watch") => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001"),
        SymbolName: "BTC/USDT",
        Kind: PriceAlertKind.Price,
        Trigger: AlertTrigger.Above,
        TargetPrice: 110_000.5m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: note,
        Status: status,
        CreatedUtc: Base,
        SnoozedUntilUtc: status == AlertStatus.Snoozed ? Base.AddMinutes(15) : null,
        LastFiredUtc: status == AlertStatus.Fired ? Base.AddMinutes(30) : null);

    private static AlertDefinition PercentAlert() => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000002"),
        SymbolName: "ETH/USDT",
        Kind: PriceAlertKind.PercentMove,
        Trigger: AlertTrigger.At,
        TargetPrice: 0m,
        ThresholdPercent: 7.5m,
        WindowMinutes: 3_600,
        Note: string.Empty,
        Status: AlertStatus.Armed,
        CreatedUtc: Base,
        SnoozedUntilUtc: null,
        LastFiredUtc: null);

    private static AlertDefinition IndicatorCrossAlert(
        AlertStatus status = AlertStatus.Armed,
        AlertTrigger trigger = AlertTrigger.Above,
        AlertCrossSource source = AlertCrossSource.IndicatorValue,
        decimal level = 70m) => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000003"),
        SymbolName: "BTC/USDT",
        Kind: PriceAlertKind.IndicatorCross,
        Trigger: trigger,
        TargetPrice: 0m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        Status: status,
        CreatedUtc: Base,
        SnoozedUntilUtc: null,
        LastFiredUtc: null,
        Indicator: new AlertIndicatorSpec("RSI", 14, source, level));

    [Fact]
    public void RoundTrip_TheCrossKindSurvivesEveryFact()
    {
        var roster = new[]
        {
            IndicatorCrossAlert(),
            IndicatorCrossAlert(
                status: AlertStatus.Paused,
                trigger: AlertTrigger.Below,
                source: AlertCrossSource.Price,
                level: 0m),
        };

        var decoded = AlertCodec.Decode(AlertCodec.Encode(roster));

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().Equal(roster, "version 2 carries the cross facts and the Paused state verbatim");
    }

    [Fact]
    public void AVersion1EntryClaimingTheCrossKind_IsSkipped()
    {
        // The cross facts cannot exist in the version 1 shape: the entry is
        // skipped with its count, never defaulted into a half-honored row.
        var payload = Roster(
            "IndicatorCross",
            CrossMembers,
            null,
            null);

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(1);
        decoded.Valid.Should().BeEmpty();
    }

    [Theory]
    [InlineData("noIndicator")]
    [InlineData("zeroPeriod")]
    [InlineData("ghostSource")]
    [InlineData("levelMissing")]
    public void ACrossEntry_WithOneBrokenSpecFact_IsSkipped(string variant)
    {
        var indicatorJson = variant switch
        {
            "noIndicator" => null,
            "zeroPeriod" => "{\"name\":\"RSI\",\"period\":0,\"source\":\"IndicatorValue\",\"level\":70}",
            "ghostSource" => "{\"name\":\"RSI\",\"period\":14,\"source\":\"Ghost\",\"level\":70}",
            "levelMissing" => "{\"name\":\"RSI\",\"period\":14,\"source\":\"IndicatorValue\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var payload = CrossRoster(indicatorJson);

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(1, "the entry cannot be honored exactly, so it is skipped, never defaulted");
        decoded.Valid.Should().BeEmpty();
    }

    [Fact]
    public void ACrossEntry_WithTheMeaninglessAtEdge_IsSkipped()
    {
        var payload = CrossRoster(
            "{\"name\":\"RSI\",\"period\":14,\"source\":\"IndicatorValue\",\"level\":70}",
            trigger: "\"At\"");

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(1, "a cross reads only the Above or Below edge");
        decoded.Valid.Should().BeEmpty();
    }

    private static AlertDefinition FundingCrossAlert(
        AlertStatus status = AlertStatus.Armed,
        AlertTrigger trigger = AlertTrigger.Above,
        decimal thresholdPercent = 0.05m) => new(
        Id: Guid.Parse("0a0a0a0a-0000-0000-0000-000000000004"),
        SymbolName: "BTC/USDT:USDT",
        Kind: PriceAlertKind.FundingCross,
        Trigger: trigger,
        TargetPrice: thresholdPercent,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        Status: status,
        CreatedUtc: Base,
        SnoozedUntilUtc: status == AlertStatus.Snoozed ? Base.AddMinutes(15) : null,
        LastFiredUtc: status == AlertStatus.Fired ? Base.AddMinutes(30) : null);

    /// <summary>
    /// The funding-cross kind (T-240) round-trips every fact: the signed
    /// percent threshold rides the price slot verbatim (zero and negatives
    /// included), the cross edge and the status stamps survive, the
    /// funding entry decodes whole.
    /// </summary>
    [Fact]
    public void RoundTrip_TheFundingCrossKindSurvivesEveryFact()
    {
        var roster = new[]
        {
            FundingCrossAlert(),
            FundingCrossAlert(
                status: AlertStatus.Paused,
                trigger: AlertTrigger.Below,
                thresholdPercent: -0.05m),
        };

        var decoded = AlertCodec.Decode(AlertCodec.Encode(roster));

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().Equal(roster, "the signed thresholds and the cross edges decode verbatim");
    }

    [Fact]
    public void AFundingEntry_WithTheMeaninglessAtEdge_IsSkipped()
    {
        var decoded = AlertCodec.Decode(FundingRoster(trigger: "\"At\""));

        decoded.SkippedCount.Should().Be(1, "a cross reads only the Above or Below edge");
        decoded.Valid.Should().BeEmpty();
    }

    [Fact]
    public void AFundingEntry_WithoutItsThreshold_IsSkipped()
    {
        var decoded = AlertCodec.Decode(FundingRoster(threshold: null));

        decoded.SkippedCount.Should().Be(1, "the threshold member's presence is the decode's demand");
        decoded.Valid.Should().BeEmpty();
    }

    /// <summary>A signed (negative) threshold is an honest stored value: the entry decodes whole, never defaulted.</summary>
    [Fact]
    public void AFundingEntry_WithASignedThreshold_DecodesWhole()
    {
        var decoded = AlertCodec.Decode(FundingRoster(trigger: "\"Below\"", threshold: "-0.05"));

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().ContainSingle()
            .Which.TargetPrice.Should().Be(-0.05m);
    }

    [Fact]
    public void RoundTrip_BothKindsEveryFact_Survives()
    {
        var roster = new[]
        {
            PriceAlert(AlertStatus.Snoozed),
            PercentAlert(),
            PriceAlert(AlertStatus.Fired, note: string.Empty),
        };

        var decoded = AlertCodec.Decode(AlertCodec.Encode(roster));

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().Equal(roster);
    }

    [Fact]
    public void EmptyRoster_EncodesTheSkeleton_DecodesEmpty()
    {
        var decoded = AlertCodec.Decode(AlertCodec.Encode(new List<AlertDefinition>()));

        decoded.Valid.Should().BeEmpty();
        decoded.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void RoundTrip_TheReArmFactsSurvive()
    {
        var roster = new[]
        {
            PriceAlert(),
            PriceAlert() with
            {
                ReArm = AlertReArmMode.Cooldown,
                CooldownMinutes = 5,
                Sound = AlertSoundChoice.Silent,
            },
            PriceAlert() with { ReArm = AlertReArmMode.PerCross },
        };

        var decoded = AlertCodec.Decode(AlertCodec.Encode(roster));

        decoded.SkippedCount.Should().Be(0, "version 3 carries the re-arm facts verbatim");
        decoded.Valid.Should().Equal(roster);
    }

    [Fact]
    public void AVersion2Roster_DecodesTheReArmFactsAsDefaults()
    {
        // The existing cross roster helper pins the version-2 shape: it
        // predates the re-arm facts entirely, so they decode as the defaults
        // (the terminal one-shot with the beep).
        var decoded = AlertCodec.Decode(CrossRoster(
            "{\"name\":\"RSI\",\"period\":14,\"source\":\"IndicatorValue\",\"level\":70}"));

        decoded.SkippedCount.Should().Be(0);
        var definition = decoded.Valid.Should().ContainSingle().Subject;
        definition.ReArm.Should().Be(AlertReArmMode.OneShot);
        definition.CooldownMinutes.Should().Be(0);
        definition.Sound.Should().Be(AlertSoundChoice.Beep);
    }

    [Fact]
    public void AVersion3PriceEntry_WithItsReArmFacts_DecodesWhole()
    {
        var payload = V3PriceRoster(Array.Empty<string>());

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(0);
        var definition = decoded.Valid.Should().ContainSingle().Subject;
        definition.ReArm.Should().Be(AlertReArmMode.PerCross);
        definition.CooldownMinutes.Should().Be(0);
        definition.Sound.Should().Be(AlertSoundChoice.Silent);
    }

    [Theory]
    [InlineData("reArm")]
    [InlineData("sound")]
    public void AVersion3Entry_MissingOneReArmFact_IsSkipped(string absentMember)
    {
        var decoded = AlertCodec.Decode(V3PriceRoster([absentMember]));

        decoded.SkippedCount.Should().Be(1, "version 3 demands the re-arm facts by name, never defaults them");
        decoded.Valid.Should().BeEmpty();
    }

    [Fact]
    public void AVersion3CooldownEntry_WithoutItsMinutes_IsSkipped()
    {
        var decoded = AlertCodec.Decode(V3PriceRoster(["cooldownMinutes"], reArm: "Cooldown"));

        decoded.SkippedCount.Should().Be(1, "a cooldown re-arm cannot be honored without its window");
        decoded.Valid.Should().BeEmpty();
    }

    [Fact]
    public void AVersion3CooldownEntry_WithItsMinutes_DecodesWhole()
    {
        var payload = V3PriceRoster(Array.Empty<string>(), reArm: "Cooldown", cooldownMinutes: "5", sound: "Silent");

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(0);
        var definition = decoded.Valid.Should().ContainSingle().Subject;
        definition.ReArm.Should().Be(AlertReArmMode.Cooldown);
        definition.CooldownMinutes.Should().Be(5);
        definition.Sound.Should().Be(AlertSoundChoice.Silent);
    }

    [Fact]
    public void AVersion3OneShotEntry_JunkInItsCooldownMinutes_DecodesVerbatim()
    {
        var payload = V3PriceRoster(Array.Empty<string>(), reArm: "OneShot", cooldownMinutes: "-77");

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(0, "the other modes' cooldown minutes are unused facts, never demarking");
        decoded.Valid.Should().ContainSingle()
            .Which.CooldownMinutes.Should().Be(-77);
    }

    [Theory]
    [InlineData("\"PerCross\"")]
    [InlineData("\"percross\"")]
    public void TheReArmEnums_ParseCaseInsensitively(string reArmJson)
    {
        var payload = V3PriceRoster(Array.Empty<string>(), reArm: null, reArmJson: reArmJson);

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().ContainSingle()
            .Which.ReArm.Should().Be(AlertReArmMode.PerCross);
    }

    [Theory]
    [InlineData("id", "\"not-a-guid\"")]
    [InlineData("symbol", "\"\"")]
    [InlineData("status", "\"Ghost\"")]
    [InlineData("trigger", "\"Ghost\"")]
    [InlineData("targetPrice", "0")]
    public void APriceEntry_WithOneBrokenRequiredFact_IsSkipped(string member, string brokenJson) =>
        SkippedCount(Roster("Price", PriceMembers, member, brokenJson)).Should().Be(1, "the entry is skipped, never defaulted");

    [Theory]
    [InlineData("id", "\"not-a-guid\"")]
    [InlineData("thresholdPercent", "0")]
    [InlineData("windowMinutes", "0")]
    public void APercentEntry_WithOneBrokenRequiredFact_IsSkipped(string member, string brokenJson) =>
        SkippedCount(Roster("PercentMove", PercentMembers, member, brokenJson)).Should().Be(1, "the entry is skipped, never defaulted");

    [Fact]
    public void AnUnknownKindEntry_IsSkipped_NeverDefaulted() =>
        SkippedCount(Roster("Nonsense", PriceMembers, null, null)).Should().Be(1, "an unseen kind is skipped, never defaulted");

    [Fact]
    public void APercentEntry_JunkInItsUnusedPriceFacts_DecodesVerbatim()
    {
        var decoded = AlertCodec.Decode(Roster(
            "PercentMove",
            PercentMembers,
            null,
            null,
            extraMember: ("targetPrice", "-77")));

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().ContainSingle()
            .Which.TargetPrice.Should().Be(-77m, "the percent kind reads only its own facts");
    }

    [Fact]
    public void StoredEnums_AndMemberNames_ParseCaseInsensitively()
    {
        var payload = "{\"vErSiOn\":1,\"alerts\":[" +
            "{\"kind\":\"price\"," + JoinMembers([
                ("ID", "\"0a0a0a0a-0000-0000-0000-000000000001\""),
                ("Symbol", "\"BTC/USDT\""),
                ("Trigger", "\"above\""),
                ("TargetPrice", "5"),
                ("ThresholdPercent", "0"),
                ("WindowMinutes", "0"),
                ("Note", "\"\""),
                ("Status", "\"ARMED\""),
                ("CreatedUtc", BaseTicksJson)]) + "}]}";

        var decoded = AlertCodec.Decode(payload);

        decoded.SkippedCount.Should().Be(0);
        decoded.Valid.Should().ContainSingle()
            .Which.Trigger.Should().Be(AlertTrigger.Above);
    }

    [Fact]
    public void AWhollyUnreadableRoot_ThrowsForTheCaller_NeverAFabricatedRoster() =>
        FluentActions
            .Invoking(() => AlertCodec.Decode("definitely not json"))
            .Should().Throw<ArgumentException>()
            .WithMessage("*not a recognizable versioned roster*");

    private static int SkippedCount(string payload) => AlertCodec.Decode(payload).SkippedCount;

    private static readonly List<(string Member, string Json)> PriceMembers =
    [
        ("id", "\"0a0a0a0a-0000-0000-0000-000000000001\""),
        ("symbol", "\"BTC/USDT\""),
        ("targetPrice", "110000.5"),
        ("trigger", "\"Above\""),
        ("status", "\"Armed\""),
        ("createdUtc", null!),
    ];

    private static readonly List<(string Member, string Json)> PercentMembers =
    [
        ("id", "\"0a0a0a0a-0000-0000-0000-000000000002\""),
        ("symbol", "\"ETH/USDT\""),
        ("thresholdPercent", "7.5"),
        ("windowMinutes", "3600"),
        ("status", "\"Armed\""),
        ("createdUtc", null!),
    ];

    private static readonly List<(string Member, string Json)> CrossMembers =
    [
        ("id", "\"0a0a0a0a-0000-0000-0000-000000000003\""),
        ("symbol", "\"BTC/USDT\""),
        ("targetPrice", "0"),
        ("trigger", "\"Above\""),
        ("status", "\"Armed\""),
        ("createdUtc", null!),
    ];

    private static readonly List<(string Member, string Json)> FundingMembers =
    [
        ("id", "\"0a0a0a0a-0000-0000-0000-000000000004\""),
        ("symbol", "\"BTC/USDT:USDT\""),
        ("trigger", "\"Above\""),
        ("status", "\"Armed\""),
        ("createdUtc", null!),
    ];

    /// <summary>
    /// Build one version-2 roster whose single entry is a funding-cross
    /// carrying the named threshold (or none at all — the missing-threshold
    /// fact); the trigger spelling is overridable for the At-edge fact.
    /// </summary>
    private static string FundingRoster(string trigger = "\"Above\"", string? threshold = "0.05")
    {
        var members = FundingMembers
            .Select(entry => entry.Member == "createdUtc" ? (entry.Member, BaseTicksJson) : entry)
            .Select(entry => entry.Member == "trigger" ? (entry.Member, trigger) : entry)
            .ToList();
        if (threshold is { } value)
        {
            members.Add(("targetPrice", value));
        }

        return $"{{\"version\":2,\"alerts\":[{{\"kind\":\"FundingCross\",{JoinMembers(members)}}}]}}";
    }

    /// <summary>
    /// Build one version-2 roster whose single entry is an indicator-cross
    /// carrying the named nested spec (or none at all); the trigger spelling
    /// is overridable for the At-edge fact.
    /// </summary>
    private static string CrossRoster(string? indicatorJson, string trigger = "\"Above\"")
    {
        var members = CrossMembers
            .Select(entry => entry.Member == "createdUtc" ? (entry.Member, BaseTicksJson) : entry)
            .Select(entry => entry.Member == "trigger" ? (entry.Member, trigger) : entry)
            .ToList();
        if (indicatorJson is { } spec)
        {
            members.Add(("indicator", spec));
        }

        return $"{{\"version\":2,\"alerts\":[{{\"kind\":\"IndicatorCross\",{JoinMembers(members)}}}]}}";
    }

    /// <summary>
    /// Build one versioned-roster payload whose single entry carries the
    /// named kind's member list with one member broken (or none, for the
    /// unknown-kind fact); an extra member rides after the shaped set (the
    /// junk-in-unused-facts fact needs a member the kind ignores).
    /// </summary>
    private static string Roster(
        string kind,
        List<(string Member, string Json)> members,
        string? brokenMember,
        string? brokenJson,
        (string Member, string Json)? extraMember = null)
    {
        var shaped = members
            .Select(entry => entry.Member == "createdUtc" ? (entry.Member, BaseTicksJson) : entry)
            .Select(entry => entry.Member == brokenMember ? (entry.Member, brokenJson!) : entry);
        if (extraMember is { } extra)
        {
            shaped = shaped.Append(extra);
        }

        return $"{{\"version\":1,\"alerts\":[{{\"kind\":\"{kind}\",{JoinMembers(shaped.ToList())}}}]}}";
    }

    private static string JoinMembers(IReadOnlyList<(string Member, string Json)> members) =>
        string.Join(",", members.Select(entry => $"\"{entry.Member}\":{entry.Json}"));

    /// <summary>
    /// Build one version-3 roster whose single entry is a Price alert with
    /// the re-arm facts shaped in (or with named members absent — the skip
    /// facts demand exact presence); the named overrides ride in as raw
    /// JSON, the defaults are the codec's own spellings.
    /// </summary>
    private static string V3PriceRoster(
        IReadOnlyCollection<string> absentMembers,
        string? reArm = "PerCross",
        string? cooldownMinutes = null,
        string? sound = "Silent",
        string? reArmJson = null)
    {
        var members = PriceMembers
            .Select(entry => entry.Member == "createdUtc" ? (entry.Member, BaseTicksJson) : entry)
            .ToList();
        members.Add(("reArm", reArmJson ?? $"\"{reArm}\""));
        if (cooldownMinutes is { } minutes)
        {
            members.Add(("cooldownMinutes", minutes));
        }

        if (sound is { } choice)
        {
            members.Add(("sound", $"\"{choice}\""));
        }

        var shaped = members.Where(entry => !absentMembers.Contains(entry.Member)).ToList();
        return $"{{\"version\":3,\"alerts\":[{{\"kind\":\"Price\",{JoinMembers(shaped)}}}]}}";
    }
}
