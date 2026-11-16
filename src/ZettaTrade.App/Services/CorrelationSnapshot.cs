namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Enums;

/// <summary>
/// One correlation pass's published table (T-252): the symbol order the
/// matrix reads in (the normalized texts, ordinal-sorted), the row-major
/// N×N coefficients (null = the honest dash — a pair the shared coverage
/// cannot judge) and the truncation flag (a roster over the cap answers
/// its first <see cref="CorrelationService.MaxSymbols"/> symbols and says
/// so). Immutable: the window's panel state moves by whole snapshots,
/// never by patching cells.
/// </summary>
public sealed record CorrelationSnapshot(
    TimeFrame Frame,
    int Window,
    IReadOnlyList<string> Symbols,
    double?[,] Matrix,
    bool Truncated);
