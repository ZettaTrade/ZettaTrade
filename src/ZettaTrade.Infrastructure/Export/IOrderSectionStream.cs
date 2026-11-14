namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Entities;

/// <summary>
/// One open order-side section pair (T-219): the statement's journal-orders
/// CSV and the fills extracted from it, both held open across the scan so
/// each appended page lands in its file row by row — the streaming shape
/// that keeps a six-figure journal at one page of memory instead of
/// materializing the whole order list before the first byte. Disposal
/// flushes and closes both; the rows already written stay on disk (no
/// partial row is ever produced, and the manifest-last rule keeps a torn
/// bundle detectable).
/// </summary>
public interface IOrderSectionStream : IAsyncDisposable
{
    /// <summary>
    /// Append one scanned page of journal orders: every order joins the
    /// orders section, every order that carries an execution joins the
    /// fills section — the same mappers and the same row order the one-shot
    /// exports speak, so the streamed bytes stay identical to theirs.
    /// </summary>
    /// <param name="page">The scanned orders to append, in export order.</param>
    /// <param name="cancellationToken">Cancels the append; rows already
    /// written stay on disk (the cancellation point sits between rows).</param>
    Task AppendAsync(IReadOnlyList<Order> page, CancellationToken cancellationToken = default);
}
