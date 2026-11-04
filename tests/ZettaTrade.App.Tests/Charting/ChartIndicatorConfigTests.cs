namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// The tolerant parse matrix of the persisted indicator rows: numbers as
/// numbers or strings, booleans as real booleans or "true"/"false", unknown
/// keys ignored, every unusable row skipped with a reported problem and
/// never clamped or defaulted.
/// </summary>
public sealed class ChartIndicatorConfigTests
{
    private const string DefaultJson = """[{"name":"SMA","enabled":true,"period":20}]""";

    [Fact]
    public void ParseList_TheDefaultShape_ParsesOneRow()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(DefaultJson);

        problems.Should().BeEmpty();
        configs.Should().ContainSingle().Which.Should().Be(
            new ChartIndicatorConfig("SMA", true, 20));
    }

    [Fact]
    public void ParseList_TolerantTypes_StillParse()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":"true","period":"8"}]""");

        problems.Should().BeEmpty();
        configs.Should().ContainSingle().Which.Should().Be(new ChartIndicatorConfig("SMA", true, 8));
    }

    [Fact]
    public void ParseList_UnknownKeys_AreIgnored()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":true,"period":20,"future":"x"}]""");

        problems.Should().BeEmpty();
        configs.Should().ContainSingle().Which.Name.Should().Be("SMA");
    }

    [Fact]
    public void ParseList_ARowMissingItsPeriod_IsSkippedWithAProblem()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":true}]""");

        configs.Should().BeEmpty("nothing defaults");
        problems.Should().ContainSingle().Which.Should().Contain("period");
    }

    [Fact]
    public void ParseList_ARowWithoutAName_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"enabled":true,"period":20}]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("name");
    }

    [Fact]
    public void ParseList_AnEmptyName_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"  ","enabled":true,"period":20}]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("name");
    }

    [Fact]
    public void ParseList_ANonIntegerPeriod_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":true,"period":"abc"}]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("period");
    }

    [Fact]
    public void ParseList_AFractionalPeriod_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":true,"period":20.5}]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("period");
    }

    [Fact]
    public void ParseList_ANumericEnabledFlag_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":1,"period":20}]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("enabled");
    }

    [Fact]
    public void ParseList_ANonObjectRow_IsSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList("""[42]""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("not a JSON object");
    }

    [Fact]
    public void ParseList_ANonArrayRoot_ReportsOneProblem()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList("""{"name":"SMA"}""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("array");
    }

    [Fact]
    public void ParseList_MalformedJson_ReportsOneProblem()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList("""[{"name":""");

        configs.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("JSON");
    }

    [Fact]
    public void ParseList_BlankValues_AreAnEmptyConfiguration()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(null);
        configs.Should().BeEmpty();
        problems.Should().BeEmpty();

        (configs, problems) = ChartIndicatorConfig.ParseList("   ");
        configs.Should().BeEmpty();
        problems.Should().BeEmpty();
    }

    [Fact]
    public void ParseList_OnlyTheBrokenRows_AreSkipped()
    {
        var (configs, problems) = ChartIndicatorConfig.ParseList(
            """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA"}]""");

        problems.Should().ContainSingle();
        configs.Should().ContainSingle().Which.Should().Be(new ChartIndicatorConfig("SMA", true, 20));
    }

    [Fact]
    public void Write_TheRowsRoundTrip()
    {
        var rows = new List<ChartIndicatorConfig>
        {
            new("SMA", true, 20),
            new("EMA", false, 9),
        };

        var (configs, problems) = ChartIndicatorConfig.ParseList(ChartIndicatorConfig.Write(rows));

        problems.Should().BeEmpty();
        configs.Should().Equal(rows);
    }

    [Fact]
    public void Write_TheShape_IsTheDocumentedCompactJson()
    {
        ChartIndicatorConfig.Write([new ChartIndicatorConfig("SMA", true, 20)])
            .Should().Be("""[{"name":"SMA","enabled":true,"period":20}]""");
    }
}
