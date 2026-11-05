namespace ZettaTrade.Infrastructure.Secrets;

using System.Collections.Generic;

/// <summary>
/// The attribute-output parsers shared by the CLI-backed platform secret
/// stores: both <see cref="MacOSKeychainSecretStore"/> and
/// <see cref="LinuxSecretServiceStore"/> resolve their stored keys by parsing
/// platform tool output (a keychain attribute dump, a secret-tool search), so
/// the parsing lives as pure functions outside the platform guard and stays
/// covered by tests everywhere (BUG-036).
/// </summary>
internal static class SecretListOutputParsers
{
    /// <summary>
    /// Extract the account attribute of every generic-password item whose
    /// service attribute equals the given service name, from the
    /// <c>security dump-keychain</c> attribute dump. One item record per
    /// <c>class:</c> line; attribute lines pair a quoted name and a quoted
    /// value across an equals sign, such as "acct" (blob) = "value". Unknown
    /// or malformed records are skipped, never guessed.
    /// </summary>
    /// <param name="dump">The full stdout of <c>security dump-keychain</c>.</param>
    internal static IReadOnlyList<string> ParseKeychainDumpAccounts(string dump)
    {
        var accounts = new List<string>();
        var serviceMatches = false;
        string? account = null;

        // Each item record ends with its class line: flush the buffered
        // attributes as one item and start the next one.
        void FlushAccount()
        {
            if (serviceMatches && account is not null)
            {
                accounts.Add(account);
            }

            serviceMatches = false;
            account = null;
        }

        foreach (var rawLine in dump.Split('\n'))
        {
            // Attribute records are indented in real dump output: the trim
            // exposes the leading attribute name to the prefix checks.
            var line = rawLine.Trim();

            if (line.StartsWith("class:", System.StringComparison.Ordinal))
            {
                FlushAccount();
                continue;
            }

            // Attribute value: the part after the first '="' quote pair up to
            // the closing quote of the line.
            var assignmentIndex = line.IndexOf("=\"", System.StringComparison.Ordinal);
            if (assignmentIndex < 0 || !line.EndsWith("\"", System.StringComparison.Ordinal))
            {
                continue;
            }

            var name = line[..assignmentIndex];
            var value = line[(assignmentIndex + 2)..^1];

            if (name.StartsWith("\"svce\"", System.StringComparison.Ordinal) &&
                value == ZettaTrade.Core.Constants.SecretStoreConstants.ServiceName)
            {
                serviceMatches = true;
            }
            else if (name.StartsWith("\"acct\"", System.StringComparison.Ordinal))
            {
                account = value;
            }
        }

        FlushAccount();
        return accounts;
    }

    /// <summary>
    /// Extract our stored keys from <c>secret-tool search --all service
    /// (service-name)</c> output: one item per blank-line-separated block of
    /// <c>attribute = value</c> lines; a block counts when its <c>service</c>
    /// attribute matches and it carries the key attribute. Unknown or
    /// malformed blocks are skipped, never guessed.
    /// </summary>
    /// <param name="search">The full stdout of <c>secret-tool search --all</c>.</param>
    internal static IReadOnlyList<string> ParseSecretToolSearchKeys(string search)
    {
        var keys = new List<string>();

        foreach (var rawBlock in search.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var serviceMatches = false;
            string? key = null;

            foreach (var rawLine in rawBlock.Split('\n'))
            {
                var line = rawLine.Trim();
                var separator = line.IndexOf(" = ", System.StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                var attribute = line[..separator];
                var value = line[(separator + 3)..];

                if (attribute == "service")
                {
                    serviceMatches = value == ZettaTrade.Core.Constants.SecretStoreConstants.ServiceName;
                }
                else if (attribute == ZettaTrade.Core.Constants.SecretStoreConstants.DefaultAttributeKey)
                {
                    key = value;
                }
            }

            if (serviceMatches && key is not null)
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
