namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The one-shot trade statement bundle (T-210), streamed (T-219): the
/// scanned journal orders ride their section CSVs page by page DURING the
/// scan — a six-figure journal never materializes the whole order list
/// before the first byte lands — while the fill-pair realized-trade ledger
/// and the balance snapshot join at the end of the scan and the JSON
/// manifest is written LAST, so a bundle interrupted mid-write is
/// detectable: a manifest on disk means every section behind it landed
/// whole.
/// </summary>
/// <remarks>
/// The destination is the MANIFEST's path; the CSV sections ride as
/// siblings of the same base name, suffixed ".orders.csv" and so on.
/// The session shape: BeginAsync opens the order-side sections (the CSV
/// headers land), every scanned page appends into them, and CompleteAsync
/// writes the realized ledger, the balances and — last — the manifest.
/// Abandoning the session (a scan fault, a cancelled user) disposes it:
/// the sections already written stay on disk, the manifest never lands.
/// </remarks>
public interface IStatementExportService
{
    /// <summary>Begin the statement bundle: open the order-side sections
    /// (the CSV headers land) and return the streaming session; the
    /// manifest is written only by
    /// <see cref="IStatementExportSession.CompleteAsync"/>.</summary>
    /// <param name="manifestPath">The manifest's destination path; the CSV
    /// sections derive their names from its base.</param>
    /// <param name="header">The statement's filter echo — the filter applied
    /// to the scan that feeds the session, written into the manifest
    /// verbatim.</param>
    /// <param name="cancellationToken">Cancels the open; the files already
    /// opened stay on disk.</param>
    Task<IStatementExportSession> BeginAsync(
        string manifestPath,
        StatementExportHeader header,
        CancellationToken cancellationToken = default);
}
