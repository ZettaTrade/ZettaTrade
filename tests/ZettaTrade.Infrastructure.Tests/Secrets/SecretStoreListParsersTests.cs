namespace ZettaTrade.Infrastructure.Tests.Secrets;

using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Infrastructure.Secrets;

/// <summary>
/// T-049 (BUG-036) listing parsers: both CLI-backed stores resolve real
/// stored keys by parsing platform tool output — the macOS keychain attribute
/// dump and the Linux <c>secret-tool search --all</c> attribute blocks. The
/// parsers are pure functions, covered everywhere; the process invocation
/// around them stays platform-guarded in the stores themselves.
/// </summary>
public sealed class SecretStoreListParsersTests
{
    #region macOS keychain dump parsing

    /// <summary>
    /// A realistic <c>security dump-keychain</c> attribute excerpt: two items
    /// belonging to our service (the first one, and a later inet item) plus an
    /// item of another application that must be skipped.
    /// </summary>
    private const string KeychainDump = """
        keychain: "/Users/trader/Library/Keychains/login.keychain-db"
        version: 512
        class: "genp"
        attributes:
            "acct"<blob>="binance-main"
            "cdat"<timedate>="2026-01-01 00:00:00 +0000"
            "svce"<blob>="ZettaTrade"
        class: "genp"
        attributes:
            "acct"<blob>="other-app-account"
            "svce"<blob>="SomeOtherApp"
        class: "inet"
        attributes:
            "acct"<blob>="bybit-main"
            "svce"<blob>="ZettaTrade"
        """;

    [Fact]
    public void TheMacOsDumpParser_ReturnsOnlyOurServiceAccountsInRecordOrder()
    {
        SecretListOutputParsers.ParseKeychainDumpAccounts(KeychainDump)
            .Should().Equal("binance-main", "bybit-main");
    }

    [Fact]
    public void TheMacOsDumpParser_SkipsMalformedLinesAndAnUnfinishedTail()
    {
        const string dump = """
            not an attribute line
            "acct"<blob>="unfinished-but-no-service"
            class: "genp"
            attributes:
                "acct"<blob>="orphan-account"
                "svce"<blob>="ZettaTrade"
            """;

        SecretListOutputParsers.ParseKeychainDumpAccounts(dump)
            .Should().Equal("orphan-account");
    }

    [Fact]
    public void TheMacOsDumpParser_TreatsAnEmptyDumpAsNoKeys()
    {
        SecretListOutputParsers.ParseKeychainDumpAccounts(string.Empty)
            .Should().BeEmpty();
    }

    #endregion

    #region Linux secret-tool search parsing

    /// <summary>
    /// A realistic <c>secret-tool search --all service ZettaTrade</c> excerpt:
    /// two items of our service separated by blank lines, one item of another
    /// application in between.
    /// </summary>
    private const string SecretToolSearch = """
        service = ZettaTrade
        key = binance-main

        service = OtherDaemon
        key = foreign-key

        service = ZettaTrade
        key = xrp-main
        """;

    [Fact]
    public void TheLinuxSearchParser_ReturnsOnlyOurServiceKeysInBlockOrder()
    {
        SecretListOutputParsers.ParseSecretToolSearchKeys(SecretToolSearch)
            .Should().Equal("binance-main", "xrp-main");
    }

    [Fact]
    public void TheLinuxSearchParser_KeepsValuesWhereTheAttributeNameIsAMerePrefix()
    {
        const string search = """
            service = ZettaTrade
            key = a-plain-key

            service = ZettaTrade
            key = a-value-with=equals
            """;

        SecretListOutputParsers.ParseSecretToolSearchKeys(search)
            .Should().Equal(new[] { "a-plain-key", "a-value-with=equals" },
                "only the first ' = ' separator splits a line");
    }

    [Fact]
    public void TheLinuxSearchParser_SkipsBlocksThatCarryNoKeyOrNoServiceMatch()
    {
        const string search = """
            service = ZettaTrade

            key = orphan-no-service
            """;

        SecretListOutputParsers.ParseSecretToolSearchKeys(search)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheLinuxSearchParser_TreatsAnEmptyOutputAsNoKeys()
    {
        SecretListOutputParsers.ParseSecretToolSearchKeys(string.Empty)
            .Should().BeEmpty();
    }

    #endregion
}
