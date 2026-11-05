namespace ZettaTrade.App.Tests.Mcp;

using FluentAssertions;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The persisted external-server list's honest round trip: unusable rows
/// are SKIPPED with a reported problem, never defaulted (a missing name or
/// an unparsable transport never silently becomes an enabled stdio entry),
/// keys match case-insensitively, transports ride both the names and the
/// numbers, and the write emits the compact camelCase shape the parse reads
/// back — the exact bytes the settings save pins.
/// </summary>
public sealed class McpServerConfigListTests
{
    #region Parse

    [Fact]
    public void Parse_ABlankValue_YieldsNothing()
    {
        var (servers, problems) = McpServerConfigList.Parse("   ");

        servers.Should().BeEmpty("nothing configured, nothing to apply");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NonJson_ReportsOneProblemAndParsesNoRows()
    {
        var (servers, problems) = McpServerConfigList.Parse("not json at all");

        servers.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("not readable JSON");
    }

    [Fact]
    public void Parse_ANonArrayRoot_ReportsOneProblemAndParsesNoRows()
    {
        var (servers, problems) = McpServerConfigList.Parse("""{"name":"stub"}""");

        servers.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("row array");
    }

    [Fact]
    public void Parse_AHappyStdioRow_ParsesEveryField()
    {
        var json = """[{"name":"local","transport":"stdio","command":"/usr/bin/mcp-echo","args":["--x","1"],"enabled":true}]""";

        var (servers, problems) = McpServerConfigList.Parse(json);

        problems.Should().BeEmpty();
        servers.Should().HaveCount(1);
        servers[0].Name.Should().Be("local");
        servers[0].Transport.Should().Be(McpTransport.Stdio);
        servers[0].Command.Should().Be("/usr/bin/mcp-echo");
        servers[0].Args.Should().Equal("--x", "1");
        servers[0].Enabled.Should().BeTrue();
        servers[0].HttpTimeoutSeconds.Should().BeNull("the option was not configured");
    }

    [Fact]
    public void Parse_AbsentEnabled_RidesDisabledByDefault()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":"stdio"}]""");

        problems.Should().BeEmpty();
        servers.Should().HaveCount(1);
        servers[0].Enabled.Should().BeFalse("an entry the user switched off must not dial");
    }

    [Fact]
    public void Parse_EnabledAsText_RidesTheBool()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":"stdio","enabled":"true"}]""");

        problems.Should().BeEmpty();
        servers[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_TheKeysMatchCaseInsensitively()
    {
        var json = """[{"NAME":"s","Transport":"streamablehttp","URL":"https://mcp.example.com:443","AuthSecretRef":"secret://k","HttpTimeoutSeconds":"9"}]""";

        var (servers, problems) = McpServerConfigList.Parse(json);

        problems.Should().BeEmpty();
        servers[0].Name.Should().Be("s");
        servers[0].Transport.Should().Be(McpTransport.StreamableHttp);
        servers[0].Url.Should().Be("https://mcp.example.com:443");
        servers[0].AuthSecretRef.Should().Be("secret://k");
        servers[0].HttpTimeoutSeconds.Should().Be(9, "the timeout option rides strings as typed");
    }

    [Fact]
    public void Parse_TransportAsNumber_RidesTheEnumValue()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":1}]""");

        problems.Should().BeEmpty();
        servers[0].Transport.Should().Be(McpTransport.StreamableHttp);
    }

    [Fact]
    public void Parse_AnUnknownTransportNumber_SkipsTheRow()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":99}]""");

        servers.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("transport");
    }

    [Fact]
    public void Parse_ARowWithoutAName_SkipsTheRow()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"transport":"stdio","command":"x"}]""");

        servers.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("no name");
    }

    [Fact]
    public void Parse_ANonObjectRow_SkipsJustTheRow()
    {
        var (servers, problems) = McpServerConfigList.Parse("""["garbage",{"name":"s","transport":"stdio"}]""");

        problems.Should().ContainSingle().Which.Should().Contain("Row 1");
        servers.Should().ContainSingle().Which.Name.Should().Be("s", "the healthy row 2 rides");
    }

    [Fact]
    public void Parse_APresentButUnreadableTimeout_SkipsTheRow()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":"stdio","httpTimeoutSeconds":"soon"}]""");

        servers.Should().BeEmpty("an unusable option never defaults to the transport's 30s silently");
        problems.Should().ContainSingle().Which.Should().Contain("httpTimeoutSeconds");
    }

    [Fact]
    public void Parse_AGarbageBoolEnabled_RidesDisabled()
    {
        var (servers, problems) = McpServerConfigList.Parse("""[{"name":"s","transport":"stdio","enabled":"also-yes"}]""");

        problems.Should().BeEmpty();
        servers[0].Enabled.Should().BeFalse();
    }

    #endregion Parse

    #region Write

    [Fact]
    public void Write_TheSettingsShape_IsTheExactCompactCamelCaseWire()
    {
        var written = McpServerConfigList.Write(
        [
            new McpServerConfig
            {
                Name = "stub",
                Transport = McpTransport.StreamableHttp,
                Url = "http://127.0.0.1:8080",
                Enabled = true,
            },
        ]);

        written.Should().Be(
            """[{"name":"stub","transport":"streamableHttp","enabled":true,"url":"http://127.0.0.1:8080"}]""",
            "the save pins exactly this wire shape");
    }

    [Fact]
    public void Write_TheAbsentOptions_StayAbsentFromTheWire()
    {
        var written = McpServerConfigList.Write(
        [
            new McpServerConfig
            {
                Name = "s",
                Transport = McpTransport.Stdio,
                Command = "mcp-echo",
                Args = ["--x", "1"],
                Enabled = false,
            },
        ]);

        written.Should().Be(
            """[{"name":"s","transport":"stdio","enabled":false,"command":"mcp-echo","args":["--x","1"]}]""",
            "no null keys, no URL, no auth reference on an stdio entry that has none");
    }

    [Fact]
    public void WriteThenParse_AFullList_RoundTrips()
    {
        var servers = new[]
        {
            new McpServerConfig
            {
                Name = "local",
                Transport = McpTransport.Stdio,
                Command = "mcp-echo",
                Args = ["--x", "1"],
                Enabled = true,
            },
            new McpServerConfig
            {
                Name = "remote",
                Transport = McpTransport.StreamableHttp,
                Url = "https://mcp.example.com:443",
                AuthSecretRef = "secret://k",
                Enabled = false,
                HttpTimeoutSeconds = 7,
            },
        };

        var (parsed, problems) = McpServerConfigList.Parse(McpServerConfigList.Write(servers));

        problems.Should().BeEmpty();
        parsed.Should().BeEquivalentTo(servers, "the list round-trips byte-honest through its own reader");
    }

    #endregion Write
}
