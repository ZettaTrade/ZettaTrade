namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One statement bundle in flight (T-219): the order-side sections
/// <see cref="IStatementExportService.BeginAsync"/> opened, fed page by
/// page during the scan, and completed after it. The manifest rides
/// <see cref="CompleteAsync"/> and is written LAST: a bundle interrupted
/// mid-write has no manifest, so a consumer treats the manifest's presence
/// as the bundle's completeness marker. Abandoning the session (a scan
/// fault, a cancelled user) disposes it: the sections already written stay
/// on disk, the manifest never lands.
/// </summary>
public interface IStatementExportSession : IAsyncDisposable
{
    /// <summary>
    /// Append one scanned page of journal orders to the order-side
    /// sections: every order joins the orders section, every order that
    /// carries an execution joins the fills section.
    /// </summary>
    /// <param name="page">The scanned orders to append, in export order.</param>
    /// <param name="cancellationToken">Cancels the append; rows already
    /// written stay on disk (the cancellation point sits between rows).</param>
    Task AppendOrdersPageAsync(IReadOnlyList<Order> page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete the bundle: the realized ledger and the balances ride their
    /// end-of-scan reads, and the manifest — carrying the filter echo and
    /// the per-file row counts — is written LAST. The session is spent
    /// after this; disposing it again is a quiet no-op.
    /// </summary>
    /// <param name="tail">The end-of-scan payload: the generation moment,
    /// the realized ledger, the balances snapshot.</param>
    /// <param name="cancellationToken">Cancels the tail writes; the files
    /// already written stay on disk.</param>
    Task CompleteAsync(StatementExportTail tail, CancellationToken cancellationToken = default);
}
