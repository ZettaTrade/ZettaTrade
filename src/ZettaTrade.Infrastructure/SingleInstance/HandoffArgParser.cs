namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// The handoff argument grammar: an allowlist walk of one launch's argv.
/// Every token must belong to the accepted set — the two flags (split and
/// attached-at-the-equals forms), and protocol strings on the handoff
/// scheme's open route. Anything else — which necessarily includes every
/// order-shaped token like <c>--order</c>, <c>--cancel</c>, side, quantity
/// or price flags — is refused by position, never inspected further, and
/// never substituted. The walk cares about shape only: symbol and exchange
/// VALIDATION is the receiver's job (the real parsers judge those values),
/// and the same parse runs on both sides (a second start before framing,
/// the running instance after the payload lands).
/// </summary>
public static class HandoffArgParser
{
    /// <summary>The symbol flag (value may ride split or attached).</summary>
    private const string SymbolFlag = "--symbol";

    /// <summary>The exchange flag (value may ride split or attached).</summary>
    private const string ExchangeFlag = "--exchange";

    /// <summary>The handoff URI scheme the optional protocol strings live on.</summary>
    private const string ProtocolScheme = "zetta";

    /// <summary>The scheme separator starting a handoff protocol string.</summary>
    private const string ProtocolSeparator = "://";

    /// <summary>The single allowlisted protocol route host.</summary>
    private const string ProtocolOpenHost = "open";

    /// <summary>The allowlisted protocol query key carrying the symbol.</summary>
    private const string ProtocolSymbolKey = "symbol";

    /// <summary>The allowlisted protocol query key carrying the exchange id.</summary>
    private const string ProtocolExchangeKey = "exchange";

    /// <summary>
    /// Parse one launch's argument vector through the allowlist grammar.
    /// A null or empty vector parses cleanly with no selection — the plain
    /// second launch (no arguments at all) is a prompt redirect like any
    /// other, it just applies nothing.
    /// </summary>
    /// <param name="args">The raw vector, exactly as the shell handed it over.</param>
    /// <returns>The grammar result: the selection or the one refusal.</returns>
    public static HandoffParsed Parse(IReadOnlyList<string>? args)
    {
        var walk = new GrammarWalk(args ?? []);
        return walk.Run();
    }

    /// <summary>Whether a token rides the handoff protocol scheme at all.</summary>
    private static bool IsProtocolString(string token) =>
        token.StartsWith(ProtocolScheme + ProtocolSeparator, StringComparison.OrdinalIgnoreCase);

    /// <summary>The disciplined walk: exactly one decision per token position, the first refusal ends it.</summary>
    private sealed class GrammarWalk
    {
        private readonly IReadOnlyList<string> _args;

        private string? _symbolText;

        private string? _exchangeId;

        public GrammarWalk(IReadOnlyList<string> args) => _args = args;

        public HandoffParsed Run()
        {
            var index = 0;
            while (index < _args.Count)
            {
                var decision = WalkToken(ref index);
                if (decision is not null)
                {
                    return HandoffParsed.Rejected(decision.Value);
                }
            }

            return BuildSelection();
        }

        /// <summary>One token position's verdict: a refusal reason, or null when the token is allowlisted.</summary>
        private HandoffRejectionReason? WalkToken(ref int index)
        {
            var token = _args[index];

            if (token == SymbolFlag)
            {
                return TakeFlagValue(ref index, SymbolFlag, ref _symbolText);
            }

            if (token == ExchangeFlag)
            {
                return TakeFlagValue(ref index, ExchangeFlag, ref _exchangeId);
            }

            if (IsAttachedForm(token, SymbolFlag))
            {
                index++;
                return TakeAttachedValue(token[(SymbolFlag.Length + 1)..], ref _symbolText);
            }

            if (IsAttachedForm(token, ExchangeFlag))
            {
                index++;
                return TakeAttachedValue(token[(ExchangeFlag.Length + 1)..], ref _exchangeId);
            }

            if (IsProtocolString(token))
            {
                index++;
                return WalkProtocolString(token, ref _symbolText, ref _exchangeId);
            }

            return HandoffRejectionReason.UnknownToken;
        }
        /// <summary>The split form: the flag followed by its value token, both consumed.</summary>
        private HandoffRejectionReason? TakeFlagValue(
            ref int index,
            string flag,
            ref string? target)
        {
            index++;
            if (index >= _args.Count)
            {
                return HandoffRejectionReason.MissingValue;
            }

            var value = _args[index];
            var gate = GateValue(value);
            if (gate is not null)
            {
                return gate;
            }

            // The value token is consumed with the flag: the walk resumes
            // past it (a re-examined value would refuse the vector on a
            // shape it was never asked to judge).
            index++;
            return StoreValue(value, ref target);
        }

        /// <summary>The attached form: the value rides inside the same token after the '='.</summary>
        private HandoffRejectionReason? TakeAttachedValue(string value, ref string? target)
        {
            if (value.Length == 0)
            {
                return HandoffRejectionReason.EmptyValue;
            }

            var gate = GateValue(value);
            if (gate is not null)
            {
                return gate;
            }

            return StoreValue(value, ref target);
        }

        /// <summary>Which value handed the walk a refusal instead of a token (values never refute).</summary>
        private HandoffRejectionReason? GateValue(string value)
        {
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                return HandoffRejectionReason.FlagShapedValue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return HandoffRejectionReason.EmptyValue;
            }

            return null;
        }

        /// <summary>One selection source per vector: a second contribution to a filled slot is the ambiguity refusal.</summary>
        private HandoffRejectionReason? StoreValue(string value, ref string? target)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                return HandoffRejectionReason.DuplicateSelectionSource;
            }

            target = value;
            return null;
        }

        /// <summary>
        /// One protocol string: the scheme's open route at most (the host IS
        /// the route), optional query keys drawn from the allowlist, decoded
        /// there into the selection the string carries. A routeless string
        /// (no query) is a valid no-op — the scheme handshake with nothing
        /// to apply.
        /// </summary>
        private HandoffRejectionReason? WalkProtocolString(string token, ref string? symbolSlot, ref string? exchangeSlot)
        {
            Uri uri;
            try
            {
                uri = new Uri(token);
            }
            catch (UriFormatException)
            {
                return HandoffRejectionReason.MalformedProtocol;
            }

            if (!string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, ProtocolOpenHost, StringComparison.OrdinalIgnoreCase))
            {
                return HandoffRejectionReason.UnrecognizedProtocolRoute;
            }

            if (uri.Query.Length <= 1)
            {
                return null;
            }

            return WalkProtocolQuery(uri.Query, ref symbolSlot, ref exchangeSlot);
        }

        /// <summary>The open route's query: only allowlisted keys, only non-empty decoded values.</summary>
        private HandoffRejectionReason? WalkProtocolQuery(string query, ref string? symbolSlot, ref string? exchangeSlot)
        {
            var pairs = query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var separator = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]).ToLowerInvariant();
                var rawValue = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);

                var decision = key switch
                {
                    ProtocolSymbolKey => StoreQueryValue(rawValue, ref symbolSlot),
                    ProtocolExchangeKey => StoreQueryValue(rawValue, ref exchangeSlot),
                    _ => HandoffRejectionReason.UnknownProtocolQueryKey,
                };

                if (decision is not null)
                {
                    return decision;
                }
            }

            return null;
        }

        /// <summary>The decoded protocol value joins the same single-selection discipline as the flag values.</summary>
        private HandoffRejectionReason? StoreQueryValue(string value, ref string? slot)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return HandoffRejectionReason.EmptyValue;
            }

            return StoreValue(value, ref slot);
        }

        private HandoffParsed BuildSelection()
        {
            if (_symbolText is null && _exchangeId is null)
            {
                return HandoffParsed.NoSelection;
            }

            return new HandoffParsed(
                new HandoffSelection(_symbolText, _exchangeId),
                null);
        }
    }

    private static bool IsAttachedForm(string token, string flag) =>
        token.Length > flag.Length &&
        token[flag.Length] == '=' &&
        token.StartsWith(flag, StringComparison.Ordinal);
}
