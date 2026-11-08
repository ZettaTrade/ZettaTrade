namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reactive.Subjects;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The log tail over the existing Serilog file sinks (T-101). The viewer
/// reads the files the way a user would — no new sinks, nothing writes to
/// stdout (the MCP protocol purity rule stands) — so this service is the
/// only production seam between the file store and the UI: at the
/// composition walk it seeds a bounded history ring from the newest log
/// file's last lines, then a poll loop pulls the file's growth and every
/// new byte chunk rides the redactor + the Serilog-format parser into a
/// bounded channel (the backpressure leg: a log flood drops the oldest
/// staged lines, memory stays bounded).
/// <para>
/// The single reader leg drains the channel into the bounded history ring
/// and publishes each drained batch as one <see cref="LogTailSlice"/> on
/// the lane the view model subscribes. History reads go through
/// <see cref="HistorySnapshotAsync"/> (the ring mutates only on the reader
/// leg; a read takes the guard, never a bare lock).
/// </para>
/// <para>
/// A failed read, a vanished folder or a torn file degrade to the last
/// honest state (logged, never invented lines): only newline-terminated
/// byte spans are consumed — a line straddling polls completes on the next
/// chunk. Both long-lived legs carry their own faults, so no background
/// task ever errors out of sight (the tracked-task canon).
/// </para>
/// </summary>
public sealed partial class LogTailService : IAsyncInitializable, IDisposable
{
    #region Constants

    /// <summary>The bounded history ring's capacity: the tail keeps at most
    /// this many redacted records (the display budget; eviction drops the
    /// oldest first).</summary>
    public const int MaxHistoryLines = 32_768;

    /// <summary>The bounded leg's capacity: this many redacted lines may
    /// stage in the channel while the reader lags; beyond it the OLDEST
    /// staged lines drop (<c>DropOldest</c>), so the flood's newest lines
    /// stay reachable and memory stays bounded.</summary>
    public const int BackpressureChannelCapacity = 8_192;

    /// <summary>The poll interval the growth watcher rides (the test
    /// constructor overrides it; production lines land within half a
    /// second of their write).</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>One poll reads at most this many bytes of file growth (a
    /// log storm slower than ~8 MB/second keeps up; faster ones catch up
    /// chunk by chunk with the torn-minute discipline intact).</summary>
    private const int MaxPollReadBytes = 4 * 1024 * 1024;

    /// <summary>The backlog reads at most this many file-end bytes (the
    /// seed can show only what the newest file's end carries).</summary>
    private const int MaxBacklogReadBytes = 1024 * 1024;

    /// <summary>The Serilog rolling-file naming (the composition root's
    /// sink path narrowed by the day stamp): only these are logs.</summary>
    private const string LogFileSearchPattern = "zettatrade-*.log";

    /// <summary>The timestamp shape the composition root's sink template
    /// prints; the parser pins exactly this format.</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    #endregion Constants

    #region Parser

    /// <summary>
    /// The sink template's header shape (the composition root's
    /// <c>outputTemplate</c>): timestamp, three-letter level, source
    /// context, message. Anything else is a continuation line.
    /// </summary>
    [GeneratedRegex(
        @"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}) \[(VRB|DBG|INF|WRN|ERR|FTL)\] (\S+) (.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLine();

    /// <summary>The three-letter level token → the mapped level. Serilog's
    /// Verbose maps onto the Microsoft.Extensions.Logging.Trace level.</summary>
    private static readonly IReadOnlyDictionary<string, LogLevel> LevelTokens =
        new Dictionary<string, LogLevel>(StringComparer.Ordinal)
        {
            ["VRB"] = LogLevel.Trace,
            ["DBG"] = LogLevel.Debug,
            ["INF"] = LogLevel.Information,
            ["WRN"] = LogLevel.Warning,
            ["ERR"] = LogLevel.Error,
            ["FTL"] = LogLevel.Critical,
        };

    /// <summary>
    /// Parse + redact raw lines into <see cref="LogLine"/> records. A line
    /// matching the header shape becomes its own record; any non-header
    /// line continues the previous header's entry (stack traces keep their
    /// moment/level/source, so level and source filters stay coherent
    /// across them). The FIRST raw line, when it carries no header, stands
    /// heading-less — nothing is guessed.
    /// </summary>
    /// <param name="rawLines">The raw (not yet redacted) lines, oldest first.</param>
    /// <returns>The redacted records, oldest first.</returns>
    internal static IReadOnlyList<LogLine> RedactAndParse(IReadOnlyList<string> rawLines)
    {
        var parsed = new List<LogLine>(rawLines.Count);

        foreach (var raw in rawLines)
        {
            var redacted = LogRedactor.Redact(raw);

            if (HeaderLine().Match(redacted) is { Success: true } header)
            {
                var moment = DateTimeOffset.ParseExact(
                    $"{header.Groups[1].Value} {header.Groups[2].Value} {header.Groups[3].Value}",
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);

                parsed.Add(new LogLine(
                    moment,
                    LevelTokens[header.Groups[4].Value],
                    header.Groups[5].Value,
                    header.Groups[6].Value,
                    IsContinuation: false));
                continue;
            }

            if (parsed.Count > 0 && !parsed[^1].IsContinuation)
            {
                parsed.Add(parsed[^1] with
                {
                    Message = redacted,
                    IsContinuation = true,
                });
                continue;
            }

            parsed.Add(LogLine.HeadinglessContinuation(redacted));
        }

        return parsed;
    }

    #endregion Parser

    #region Private Fields

    private readonly ILogger<LogTailService> _logger;
    private readonly string _logDirectory;
    private readonly TimeSpan _pollInterval;

    /// <summary>The drained-slice lane; the viewer subscribes per open.</summary>
    private readonly Subject<LogTailSlice> _slices = new();

    /// <summary>The bounded backpressure leg between the poll and the reader.</summary>
    private readonly Channel<LogLine> _channel = Channel.CreateBounded<LogLine>(
        new BoundedChannelOptions(BackpressureChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

    /// <summary>The guard around history reads (no bare lock in async code).</summary>
    private readonly SemaphoreSlim _historyGuard = new(1, 1);

    /// <summary>The bounded history ring; mutated only on the reader leg.</summary>
    private readonly Queue<LogLine> _history = new(MaxHistoryLines);

    /// <summary>The tracked long-lived legs; each carries its own faults.</summary>
    private Task? _producer;
    private Task? _reader;

    /// <summary>The byte offset of the last consumed newline boundary.</summary>
    private long _tailOffset;

    /// <summary>The file currently tailed (the day roll switches it).</summary>
    private string? _tailFile;

    private readonly CancellationTokenSource _stopping = new();

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the tail over the platform log directory.
    /// </summary>
    /// <param name="logger">The service's own logger.</param>
    public LogTailService(ILogger<LogTailService> logger)
        : this(logger, ConfigPathResolver.LogDirectory, DefaultPollInterval)
    {
    }

    /// <summary>
    /// The explicit-directory test constructor: the facts pin the tail
    /// against fixture files, never the developer machine's real logs.
    /// </summary>
    /// <param name="logger">The service's own logger.</param>
    /// <param name="logDirectory">The directory holding the log files.</param>
    /// <param name="pollInterval">The growth-watch interval.</param>
    internal LogTailService(
        ILogger<LogTailService> logger,
        string logDirectory,
        TimeSpan pollInterval)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "The poll interval must be positive.");
        }

        _pollInterval = pollInterval;
    }

    #endregion Constructor

    #region Public API

    /// <summary>The directory the tail reads from.</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>The file currently tailed (null before the tail started).</summary>
    public string? TailFile => _tailFile;

    /// <summary>The drained-slice lane (one event per drained channel batch).</summary>
    public IObservable<LogTailSlice> Slices => _slices;

    /// <summary>
    /// Seed the bounded history ring with the newest existing log file's
    /// last lines, then start the tail legs. The seed is the composition
    /// walk's once-per-launch backlog; from there on the ring always holds
    /// the newest <see cref="MaxHistoryLines"/> redacted records.
    /// </summary>
    /// <param name="ct">The token the walk cancels on.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var backlog = await ReadBacklogAsync(ct);

        await AppendToHistoryAsync(RedactAndParse(backlog), evict: false, ct);

        StartTailLoop();
    }

    /// <summary>
    /// Read the bounded history ring's newest lines, oldest first. The
    /// viewer calls this ONCE per open right behind the lane attach —
    /// slices crossing the attach deduplicate inside the view model (the
    /// attach / seed discipline).
    /// </summary>
    /// <param name="ct">The caller's token.</param>
    /// <returns>The ring's snapshot, oldest first.</returns>
    public async Task<IReadOnlyList<LogLine>> HistorySnapshotAsync(CancellationToken ct = default)
    {
        await _historyGuard.WaitAsync(ct);

        try
        {
            var snapshot = new LogLine[_history.Count];
            _history.CopyTo(snapshot, arrayIndex: 0);
            return snapshot;
        }
        finally
        {
            _historyGuard.Release();
        }
    }

    /// <summary>
    /// Stop both legs and drop the lane. A process-lifetime singleton in
    /// production; the contract exists so the legs never outlive the owner
    /// (tests dispose the service deterministically).
    /// </summary>
    public void Dispose()
    {
        _stopping.Dispose();
        _slices.Dispose();
        _historyGuard.Dispose();
    }

    #endregion Public API

    #region Initialize + Tail Loop

    private void StartTailLoop()
    {
        var ct = _stopping.Token;

        _reader = Task.Run(() => ReadChannelLoopAsync(ct), CancellationToken.None);
        _producer = Task.Run(() => PollFileGrowthAsync(ct), CancellationToken.None);
    }

    /// <summary>
    /// The reader leg: drain the channel in whole batches (everything
    /// staged at this moment), fold each batch into the bounded history
    /// ring and publish the slice. The bounded channel is this loop's only
    /// input, so the ring stays the one truthful line store.
    /// </summary>
    private async Task ReadChannelLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                var batch = new List<LogLine>();

                while (_channel.Reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                if (batch.Count > 0)
                {
                    await AppendToHistoryAsync(batch, evict: true, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The dispose path: the channel tears down with the service.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The log tail's channel reader stopped unexpectedly");
        }
    }

    /// <summary>
    /// Fold one batch into the bounded history ring: append, then evict the
    /// overflow from the head. The ring mutation happens under the read
    /// guard; the slice leaves through the lane after the release, so a
    /// slow surface stalls the lane, never the file tail.
    /// </summary>
    private async Task AppendToHistoryAsync(
        IReadOnlyList<LogLine> lines,
        bool evict,
        CancellationToken ct)
    {
        var evicted = new List<LogLine>();

        await _historyGuard.WaitAsync(ct);

        try
        {
            foreach (var line in lines)
            {
                if (evict && _history.Count >= MaxHistoryLines)
                {
                    evicted.Add(_history.Dequeue());
                }

                _history.Enqueue(line);
            }
        }
        finally
        {
            _historyGuard.Release();
        }

        _slices.OnNext(new LogTailSlice(lines, evicted));
    }

    /// <summary>
    /// The producer leg: every poll, pull the tailed file's growth since
    /// the last confirmed newline boundary into the channel (the tail
    /// reads through the sink's concurrent writes), and follow the day
    /// roll when the newest log file changes under it.
    /// </summary>
    private async Task PollFileGrowthAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // A day roll (or an external rename) re-resolves the newest
                // log file and continues from its start; the rolled content
                // stays in the older file the ring already seeded.
                var newest = ResolveNewestLogFile();
                if (Supersedes(newest, _tailFile))
                {
                    _tailFile = newest;
                    _tailOffset = 0;
                }

                if (_tailFile is null)
                {
                    continue;
                }

                var chunk = await ReadChunkAsync(_tailFile, _tailOffset, MaxPollReadBytes, ct);
                if (chunk.Lines.Count > 0)
                {
                    foreach (var line in RedactAndParse(chunk.Lines))
                    {
                        await _channel.Writer.WriteAsync(line, ct);
                    }
                }

                _tailOffset = chunk.ConfirmedEnd;
            }
        }
        catch (OperationCanceledException)
        {
            // The dispose path.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The log tail's file watcher stopped unexpectedly");
        }
    }

    #endregion Initialize + Tail Loop

    #region Reading

    /// <summary>
    /// The backlog read: the newest log file's line-carrying end bytes,
    /// split into complete lines, redacted + parsed by the caller. An empty
    /// folder (or a file shorter than one line) seeds an empty ring — the
    /// live tail takes over from wherever the sink writes next. The tail's
    /// confirmed position starts at the file's END, so nothing re-reads.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadBacklogAsync(CancellationToken ct)
    {
        var path = ResolveNewestLogFile();
        _tailFile = path;

        if (path is null || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var length = new FileInfo(path).Length;
        var start = Math.Max(0, length - MaxBacklogReadBytes);

        var chunk = await ReadChunkAsync(path, start, MaxBacklogReadBytes, ct);

        _tailOffset = length;

        // When the window began mid-file, its first line continues bytes
        // the window does not carry: the partial head never shows.
        if (start > 0 && chunk.Lines.Count > 0)
        {
            var trimmed = new List<string>(chunk.Lines.Count - 1);
            for (var i = 1; i < chunk.Lines.Count; i++)
            {
                trimmed.Add(chunk.Lines[i]);
            }

            return trimmed;
        }

        return chunk.Lines;
    }

    /// <summary>
    /// Read one byte window from an offset, split it into COMPLETE lines
    /// (the tail of the window without a newline stays un-consumed: the
    /// confirmed end stops at the last newline, so the next poll re-reads
    /// that remainder and a straddling line completes there) and report
    /// the confirmed byte end. A locked or vanished file degrades to an
    /// empty read: the state holds, nothing is invented.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="offset">The byte offset the window starts at.</param>
    /// <param name="maxBytes">The window's byte cap.</param>
    /// <param name="ct">The reading token.</param>
    /// <returns>The complete lines and the confirmed byte end.</returns>
    private async Task<(IReadOnlyList<string> Lines, long ConfirmedEnd)> ReadChunkAsync(
        string path,
        long offset,
        int maxBytes,
        CancellationToken ct)
    {
        byte[] window;
        long windowLength;

        try
        {
            await using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 0,
                FileOptions.SequentialScan))
            {
                if (stream.Length <= offset)
                {
                    return (Array.Empty<string>(), offset);
                }

                windowLength = Math.Min(stream.Length - offset, maxBytes);
                window = new byte[windowLength];
                stream.Seek(offset, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(window, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The log tail could not read {File}; keeping the previous state", path);
            return (Array.Empty<string>(), offset);
        }

        // The last newline boundary splits complete content (a line without
        // its terminator stays for the next poll).
        var confirmedEnd = Array.LastIndexOf(window, (byte)'\n') + 1;
        if (confirmedEnd == 0)
        {
            return (Array.Empty<string>(), offset);
        }

        var text = Encoding.UTF8.GetString(window, 0, confirmedEnd);
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            // The trailing split artifact after the final newline.
            var trimmed = new string[lines.Length - 1];
            Array.Copy(lines, trimmed, trimmed.Length);
            lines = trimmed;
        }

        return (lines, offset + confirmedEnd);
    }

    /// <summary>The newest log file by name (the day stamp sorts lexically).</summary>
    private string? ResolveNewestLogFile()
    {
        string? newest = null;

        try
        {
            foreach (var path in Directory.EnumerateFiles(_logDirectory, LogFileSearchPattern))
            {
                if (newest is null ||
                    string.Compare(
                        Path.GetFileName(path),
                        Path.GetFileName(newest),
                        StringComparison.OrdinalIgnoreCase) > 0)
                {
                    newest = path;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate the log directory {Directory}", _logDirectory);
        }

        return newest;
    }

    /// <summary>Whether a candidate truly supersedes the current tail file.</summary>
    private static bool Supersedes(string? candidate, string? current)
    {
        if (candidate is null)
        {
            return false;
        }

        if (current is null)
        {
            return true;
        }

        return string.Compare(
                   Path.GetFileName(candidate),
                   Path.GetFileName(current),
                   StringComparison.OrdinalIgnoreCase) > 0;
    }

    #endregion Reading
}
