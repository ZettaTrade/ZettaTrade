namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The bounded excerpt writer (T-101): writes the selected display rows
/// to a user-picked text file, one line per row in the log's own shape.
/// The rows arrive ALREADY redacted (the redaction applies at the tail's
/// ingest — no path the excerpt takes can carry a secret shape), so the
/// writer performs a faithful, bounded dump and nothing else. A cancelled
/// pick (null path) never reaches the writer.
/// </summary>
public static class LogExcerptWriter
{
    /// <summary>
    /// Write one excerpt. The caller bounded the rows (the newest slice of
    /// the matching surface); the writer renders each line as
    /// <c>{moment} [{level}] {source} {message}</c> — continuation lines
    /// carry their message only, mirroring the log's own rendering.
    /// </summary>
    /// <param name="path">The absolute file path (the picker's output).</param>
    /// <param name="lines">The bounded row set, oldest first.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns>The completed write.</returns>
    public static async Task WriteAsync(
        string path,
        IReadOnlyList<LogViewerRow> lines,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(lines);

        StringBuilder content = new();

        for (var i = 0; i < lines.Count; i++)
        {
            var row = lines[i];
            if (row.Line.IsContinuation)
            {
                content.AppendLine(row.Line.Message);
                continue;
            }

            content.AppendLine($"{row.Moment} [{row.LevelName}] {row.Line.Source} {row.Line.Message}");
        }

        await File.WriteAllTextAsync(path, content.ToString(), Encoding.UTF8, ct);
    }
}
