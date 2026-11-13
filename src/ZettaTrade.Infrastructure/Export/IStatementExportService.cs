namespace ZettaTrade.Infrastructure.Export;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The one-shot trade statement bundle (T-210): the scanned journal orders,
/// the fill ledger extracted from them, the fill-pair realized-trade ledger
/// and the traded surface's balance snapshot, written as CSV sections plus
/// a JSON manifest that ties the bundle together.
/// </summary>
/// <remarks>
/// The destination is the MANIFEST's path; the CSV sections ride as
/// siblings of the same base name, suffixed ".orders.csv" and so on.
/// The manifest is written LAST: a bundle interrupted mid-write has
/// no manifest, so a consumer treats the manifest's presence as the
/// bundle's completeness marker.
/// </remarks>
public interface IStatementExportService
{
    /// <summary>Write the statement bundle (the CSV sections, then the manifest last).</summary>
    /// <param name="manifestPath">The manifest's destination path; the CSV
    /// sections derive their names from its base.</param>
    /// <param name="statement">The statement's payload.</param>
    /// <param name="cancellationToken">Cancels the write; the files already
    /// written stay on disk (the manifest-last rule keeps a torn bundle
    /// detectable).</param>
    Task WriteAsync(
        string manifestPath,
        StatementExport statement,
        CancellationToken cancellationToken = default);
}
