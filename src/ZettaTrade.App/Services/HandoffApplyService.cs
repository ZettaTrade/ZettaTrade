namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The handoff's receiver-side worker: the semantic validator over the
/// allowlist grammar and the one application route the workspace symbol
/// context owns. The exchange id parses against the live adapter catalog
/// (unknown ids are a refusal, never a default) and the symbol text parses
/// through the real symbol parsers (the exchange's format when an exchange
/// rides along, the normalized format otherwise) — a malformed value is a
/// refusal receipt, never a substituted symbol. The application routes
/// through the exact palette precedent: a same-exchange selection writes
/// the chart's workspace symbol (the chart's own pipeline persists it), a
/// cross-exchange selection writes the workspace symbol preference first
/// and only then switches the active exchange, so the pending bootstrap
/// preselects the pair. All applies serialize behind one gate: a forwarded
/// delivery and the app's own launch arguments can never interleave.
/// Everything lands in the file logs; nothing here writes stdout.
/// </summary>
public sealed class HandoffApplyService
{
    private readonly ActiveExchangeService _activeExchange;

    private readonly ChartViewModel _chart;

    private readonly ISettingsService _settings;

    private readonly ILogger<HandoffApplyService> _logger;

    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>
    /// Create the apply worker over the facade it validates against and
    /// the chart whose workspace symbol route it routes into.
    /// </summary>
    public HandoffApplyService(
        ActiveExchangeService activeExchange,
        ChartViewModel chart,
        ISettingsService settings,
        ILogger<HandoffApplyService> logger)
    {
        _activeExchange = activeExchange ?? throw new ArgumentNullException(nameof(activeExchange));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Apply one allowlisted vector end to end. Every outcome is a settled
    /// receipt: the grammar refusal, the semantic refusal, the no-selection
    /// handshakes and the applied shapes all ride this one lane.
    /// </summary>
    /// <param name="args">The raw vector as forwarded (or the app's own launch arguments).</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The settled receipt for the file logs.</returns>
    public async Task<HandoffReceipt> ApplyAsync(
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        var parsed = HandoffArgParser.Parse(args);
        if (parsed.Rejection is not null)
        {
            _logger.LogWarning(
                "Handoff apply refused by the grammar: {Reason} ({TokenCount} token(s))",
                parsed.Rejection,
                args.Count);
            return HandoffReceipt.GrammarRefused;
        }

        if (parsed.Selection is not { HasSelection: true })
        {
            return HandoffReceipt.NoSelection;
        }

        await _applyGate.WaitAsync(ct);
        try
        {
            return await ApplySelectionAsync(parsed.Selection, ct);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// The semantic layer: validate the exchange id against the live
    /// adapter catalog, the symbol text through the real parsers, then
    /// route the application through the workspace-symbol write.
    /// </summary>
    private async Task<HandoffReceipt> ApplySelectionAsync(
        HandoffSelection selection,
        CancellationToken ct)
    {
        IMarketDataAdapter? exchange = null;
        if (selection.ExchangeId is { } exchangeId)
        {
            exchange = ResolveExchange(exchangeId);
            if (exchange is null)
            {
                _logger.LogWarning(
                    "Handoff apply refused: no registered exchange adapter has id '{ExchangeId}'",
                    exchangeId);
                return new HandoffReceipt(
                    false,
                    $"no registered exchange adapter has id '{selection.ExchangeId}'");
            }
        }

        Symbol? symbol = null;
        if (selection.SymbolText is { } symbolText)
        {
            symbol = exchange is null
                ? ParseNormalizedSymbol(symbolText)
                : ParseExchangeSymbol(symbolText, exchange.Id);
            if (symbol is null)
            {
                _logger.LogWarning(
                    "Handoff apply refused: symbol text does not parse ({HasExchangeContext})",
                    exchange is not null);
                return new HandoffReceipt(
                    false,
                    "the symbol text does not parse in the exchange context the vector carries");
            }
        }

        return await WriteAsync(symbol, exchange, ct);
    }

    /// <summary>
    /// The written route: the exchange-only handovers switch the venue; the
    /// symbol selections ride the palette precedent (a same-exchange
    /// selection writes the chart property directly, a cross-exchange one
    /// writes the preference first and only then the switch — never a
    /// second symbol write path).
    /// </summary>
    private async Task<HandoffReceipt> WriteAsync(
        Symbol? symbol,
        IMarketDataAdapter? exchange,
        CancellationToken ct)
    {
        if (symbol is null)
        {
            // Exchange-only selection: the handover moves the venue.
            await SwitchExchangeAsync(exchange!, ct);
            return new HandoffReceipt(true, $"the exchange moved to '{exchange!.Id}'");
        }

        var isSameExchange = exchange is null ||
                             string.Equals(
                                 exchange.Id,
                                 _activeExchange.Current.Id,
                                 StringComparison.OrdinalIgnoreCase);
        if (isSameExchange)
        {
            await WriteSymbolAsync(symbol, ct);
            return new HandoffReceipt(true, $"the workspace symbol read '{symbol.Normalized}'");
        }

        try
        {
            // The applied pair rides the SAME workspace symbol preference
            // the chart's setter maintains (the palette cross-exchange
            // ordering): written BEFORE the switch so the pending
            // bootstrap reads it as the preference and preselects the
            // pair once it lands.
            await _settings.SetAsync(UiSettingsKeys.WorkspaceSection, UiSettingsKeys.WorkspaceSymbolId, symbol.Normalized);
            await SwitchExchangeAsync(exchange!, ct);
        }
        catch (ArgumentException ex)
        {
            // The exchange left the registry between the validation and the
            // switch (the palette's own stale-row shape): the refusal logs
            // and the workspace stays as it stood.
            _logger.LogWarning(
                ex,
                "Handoff apply refused: the exchange is no longer registered");
            return new HandoffReceipt(false, "the exchange is no longer registered");
        }

        return new HandoffReceipt(true, $"the workspace symbol read '{symbol.Normalized}' on '{exchange!.Id}'");
    }

    /// <summary>
    /// The active-exchange switch, wrapped for the receipt language (the
    /// facade throws on an unregistered id — the belt behind the catalog
    /// check above).
    /// </summary>
    private async Task SwitchExchangeAsync(IMarketDataAdapter exchange, CancellationToken ct)
    {
        await _activeExchange.SwitchToAsync(exchange.Id, ct);
    }

    /// <summary>
    /// The same-exchange write: the chart's workspace symbol property.
    /// When the caller is the dispatch pump's background task the write
    /// marshals through the Avalonia dispatcher (the default scheduler's
    /// rule for every user-interface property write); the app's own
    /// bootstrap call is already on the user-interface thread and writes
    /// inline.
    /// </summary>
    private async Task WriteSymbolAsync(Symbol symbol, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _chart.SelectedSymbol = symbol;
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => _chart.SelectedSymbol = symbol);
    }

    /// <summary>The catalog lookup (case-insensitive); null when the id is not registered.</summary>
    private IMarketDataAdapter? ResolveExchange(string exchangeId) =>
        _activeExchange.Registered.FirstOrDefault(adapter =>
            string.Equals(adapter.Id, exchangeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The normalized-format parse (the no-exchange context); null on a malformed value.</summary>
    private static Symbol? ParseNormalizedSymbol(string symbolText)
    {
        try
        {
            return Symbol.Parse(symbolText);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The exchange-format parse (the real validator, the venue's own grammar); null on a malformed value.</summary>
    private static Symbol? ParseExchangeSymbol(string symbolText, string exchangeId)
    {
        return Symbol.TryParseExchangeFormat(symbolText, exchangeId, out var symbol)
            ? symbol
            : null;
    }
}
