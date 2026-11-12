namespace ZettaTrade.App.Services;

using System.Globalization;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Paper account reset (T-192): wipes the paper simulation scope — its
/// balances, open positions and working orders — and restarts the
/// simulation at the chosen starting balance. The session journal drains
/// its paper rows and the persistent order history deletes exactly the
/// paper venue's rows; live and testnet venues are never touched, and the
/// realized-PnL history stays (outside the named scope — the T-189 loss
/// governor resets its own counters).
/// <para>
/// The fresh adapter reads at action time are the whole truth: the plan
/// (the working-order and open-position counts the reset will wipe) is
/// counted off the paper venue's own surface right before the ONE
/// confirmation (<see cref="OrderConfirmationComposer.ForPaperReset"/>)
/// names the scope and the chosen balance. A declined confirmation is
/// silent and touches nothing. The chosen starting balance persists
/// best-effort after the reset lands: a settings fault logs and never
/// undoes the reset.
/// </para>
/// </summary>
public interface IPaperResetService
{
    /// <summary>
    /// Read the persisted starting balance — the next reset dialog's
    /// prefill. A fresh install reads the shipped default (the adapter's
    /// canonical simulation balance).
    /// </summary>
    Task<decimal> GetStartingBalanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Run the reset: the fresh plan read, the ONE confirmation, then the
    /// simulation wipe, the journal wipes and the best-effort persistence
    /// of the chosen balance.
    /// </summary>
    /// <param name="startingBalance">The balance the simulation restarts at; must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done with the summary line, or Declined when the user refused.</returns>
    Task<PaperResetOutcome> ResetAsync(decimal startingBalance, CancellationToken cancellationToken = default);
}

/// <summary>One reset attempt's outcome: done with its summary, or declined.</summary>
public sealed record PaperResetOutcome
{
    /// <summary>True when the reset ran; false when the confirmation was declined.</summary>
    public required bool Done { get; init; }

    /// <summary>The localized summary line of the finished reset (null when declined).</summary>
    public string? Summary { get; init; }

    /// <summary>The declined outcome: nothing was touched, nothing is reported.</summary>
    public static PaperResetOutcome Declined() => new() { Done = false };

    /// <summary>The finished outcome with its localized summary line.</summary>
    public static PaperResetOutcome Finished(string summary) => new() { Done = true, Summary = summary };
}

public sealed class PaperResetService : IPaperResetService
{
    #region Resx Keys

    private const string DoneSummaryKey = "Trading.PaperResetDoneFormat";

    #endregion Resx Keys

    #region Private Fields

    private readonly PaperTradingAdapter _paper;
    private readonly ISettingsService _settings;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<PaperResetService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the paper reset service over the paper venue's own adapter
    /// (by concrete type: the reset targets the simulation no matter which
    /// surface the workspace trades) and the reset's gates.
    /// </summary>
    public PaperResetService(
        PaperTradingAdapter paper,
        ISettingsService settings,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<PaperResetService> logger)
    {
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc/>
    public Task<decimal> GetStartingBalanceAsync(CancellationToken cancellationToken = default) =>
        _settings.GetAsync<decimal>(
            UiSettingsKeys.PaperSection,
            UiSettingsKeys.PaperStartingBalance,
            UiSettingsKeys.DefaultPaperStartingBalance);

    /// <inheritdoc/>
    public async Task<PaperResetOutcome> ResetAsync(
        decimal startingBalance,
        CancellationToken cancellationToken = default)
    {
        // The dialog refuses non-positive input inline; the adapter refuses
        // it too. The service repeats the check so no caller route can
        // substitute or default it (trading safety: never a default).
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startingBalance, decimal.Zero);

        // The fresh plan read: what the reset will wipe, counted off the
        // paper venue's own surface right before the confirmation names it.
        var orders = await _paper.GetOpenOrdersAsync(cancellationToken);
        var positions = await _paper.GetPositionsAsync(cancellationToken);

        // ONE confirmation gates the whole reset: the chosen balance and
        // the named scope. A refusal leaves the account untouched.
        if (!await _confirmation.RequestConfirmationAsync(
                _composer.ForPaperReset(startingBalance, orders.Count, positions.Count),
                cancellationToken))
        {
            _logger.LogInformation(
                "Paper reset: the user refused the confirmation; nothing touched ({OrderCount} order(s), {PositionCount} position(s) stay working)",
                orders.Count,
                positions.Count);
            return PaperResetOutcome.Declined();
        }

        // The simulation wipe: balances, positions, working orders, the
        // parked brackets and the conditional/OCO registries. The adapter
        // re-validates the balance under its own lock.
        await _paper.ResetSimulationAsync(startingBalance, cancellationToken);

        // The paper venue's journal rows: the session journal drains its
        // paper rows, the persistent history deletes exactly the paper
        // exchange's rows (live and testnet rows stay).
        _journal.RemoveVenue(PaperTradingAdapter.ExchangeId);
        var deletedRows = await _orderHistory.DeleteExchangeAsync(
            PaperTradingAdapter.ExchangeId,
            cancellationToken);

        // The chosen balance persists for the next session and the next
        // reset's prefill. Best effort: a settings fault logs and never
        // undoes the reset.
        try
        {
            await _settings.SetAsync(
                UiSettingsKeys.PaperSection,
                UiSettingsKeys.PaperStartingBalance,
                startingBalance);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Paper reset: the starting balance could not be persisted; the reset stays done");
        }

        _logger.LogInformation(
            "Paper reset: the simulation restarted at {StartingBalance} USDT; {DeletedRows} history row(s) deleted",
            startingBalance,
            deletedRows);
        return PaperResetOutcome.Finished(_localization.GetString(
            DoneSummaryKey,
            startingBalance.ToString(CultureInfo.InvariantCulture),
            deletedRows.ToString(CultureInfo.InvariantCulture)));
    }

    #endregion Public Methods
}
