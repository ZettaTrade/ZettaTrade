namespace ZettaTrade.App.Services;

using System;
using Microsoft.Extensions.Logging;

/// <summary>
/// One parsed + redacted line of the log tail (T-101): the record enters
/// the system only after <see cref="LogRedactor"/> has stripped every
/// secret-shaped pattern, so no consumer — the ring, the surface, the
/// export — can ever hold a raw credential string. Value equality is the
/// identity the viewer's seed/attach dedupe queue rides (two identical
/// content lines at the same millisecond render identically; keeping one
/// is the accepted posture).
/// <para>
/// Continuation lines (exception stacks and multi-line messages that carry
/// no header of their own) inherit the header fields of the line they
/// visually belong to, so level/source filters stay coherent across them.
/// A heading-less continuation (a malformed file's first line) renders
/// with <see cref="LogLevel.None"/> and an empty source and never claims a
/// default level.
/// </para>
/// </summary>
/// <param name="Moment">The header's timestamp with its recorded offset.</param>
/// <param name="Level">The mapped Microsoft.Extensions.Logging level.</param>
/// <param name="Source">The header's source context (the logger name).</param>
/// <param name="Message">The redacted message (or the redacted continuation text).</param>
/// <param name="IsContinuation">Whether the line continues the previous header's entry.</param>
public sealed record LogLine(
    DateTimeOffset Moment,
    LogLevel Level,
    string Source,
    string Message,
    bool IsContinuation)
{
    /// <summary>
    /// Build a heading-less continuation record: nothing is invented — no
    /// moment, no level, no source — the raw text stands alone on the surface.
    /// </summary>
    /// <param name="message">The redacted continuation text.</param>
    /// <returns>The standalone continuation line.</returns>
    public static LogLine HeadinglessContinuation(string message) => new(
        DateTimeOffset.MinValue,
        LogLevel.None,
        string.Empty,
        message,
        IsContinuation: true);
}

/// <summary>
/// One drained slice of the tail channel (T-101): the added lines (oldest
/// first, already redacted) plus the history rows the bounded ring evicted
/// to stay inside its capacity. The viewer applies both with single
/// multi-element collection events per slice — a flood never storms the
/// surface with one event per line.
/// </summary>
/// <param name="Appended">The lines that entered the ring, oldest first.</param>
/// <param name="Evicted">The lines the ring evicted from its head, oldest first.</param>
public sealed record LogTailSlice(
    IReadOnlyList<LogLine> Appended,
    IReadOnlyList<LogLine> Evicted);
