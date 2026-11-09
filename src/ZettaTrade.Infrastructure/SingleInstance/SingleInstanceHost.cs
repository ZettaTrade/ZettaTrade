namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// The running instance's handoff host: one named pipe whose dispatch loop
/// serves redirected starts, one server at a time, until the process ends.
/// The loop never spins (each turn awaits a connection under the pump's own
/// cancellation) and never wedges past a bounded framing read — a peer that
/// connects and then goes silent is refused at the frame bound, so the next
/// start always finds the pipe serving.
/// </summary>
/// <remarks>
/// The apply runner is the app-layer's worker (the workspace-symbol route
/// the composition wires in); the host stays transport-only: it frames,
/// trims and relays, and never inspects a value further than the grammar
/// does. The loop start lives on the app bootstrap's own call (after the
/// composition walk), so a forwarded selection can never touch a
/// half-built dependency tree.
/// </remarks>
public sealed class SingleInstanceHost
{
    /// <summary>Length of the frame's own header (a 4-byte little-endian prefix, the client's mirror).</summary>
    private const int FrameHeaderBytes = 4;

    /// <summary>The bound per pump: one connecting peer at a time, served serially.</summary>
    private const int MaxServerInstances = 1;

    /// <summary>How long a connected peer may stay silent before the frame read gives its turn up (the framing bound, not an apply bound).</summary>
    private const int PeerFrameTimeoutMs = 5_000;

    private readonly string _pipeName;

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<HandoffReceipt>> _applyRunner;

    private readonly ILogger<SingleInstanceHost> _logger;

    private readonly CancellationTokenSource _pumpCts = new();

    private int _started;

    private Task? _pumpTask;

    /// <summary>
    /// Create the host: the pipe name and the apply route, injected at the
    /// composition. Nothing listens until <see cref="Start"/>.
    /// </summary>
    /// <param name="pipeName">The handoff pipe's name (the profile's own by construction).</param>
    /// <param name="applyRunner">The app-layer caller for one allowlisted vector: parse to apply, the settled receipt as the result.</param>
    /// <param name="logger">The host's own logger (the Serilog file log; nothing hits stdout).</param>
    public SingleInstanceHost(
        string pipeName,
        Func<IReadOnlyList<string>, CancellationToken, Task<HandoffReceipt>> applyRunner,
        ILogger<SingleInstanceHost> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(applyRunner);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeName = pipeName;
        _applyRunner = applyRunner;
        _logger = logger;
    }

    /// <summary>
    /// Open the dispatch pipe and start serving. Idempotent: the single
    /// pump starts exactly once, whichever call wins. The caller is the
    /// app bootstrap, after the composition walk. The first pipe instance
    /// is constructed synchronously: the moment this method returns, the
    /// pipe exists, so a second start's first connect attempt cannot lose
    /// a race with the pump's own asynchronous warm-up.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        NamedPipeServerStream first;
        try
        {
            first = CreateServerStream();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The handoff pipe failed to come up; the dispatch pump stands down");
            return;
        }

        _pumpTask = Task.Run(() => PumpAsync(first, _pumpCts.Token), CancellationToken.None);
        _logger.LogInformation(
            "Single-instance handoff host serving pipe {PipeName}",
            _pipeName);
    }

    /// <summary>
    /// Stop serving (the process-lifecycle clean close; nothing in the app
    /// lane calls this — the pump dies with the process by OS semantics).
    /// </summary>
    public void Stop()
    {
        _pumpCts.Cancel();
    }

    /// <summary>
    /// Handle one vector end-to-end on the running instance: the grammar
    /// walk, then — when the walk yields a selection — the app-layer apply.
    /// This is the one entry both the dispatch pump and the app's own
    /// launch arguments ride, so the app's own start applies its args
    /// through exactly the route a forwarded start does.
    /// </summary>
    /// <param name="args">The raw vector.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The settled receipt for the file logs.</returns>
    public async Task<HandoffReceipt> ProcessAsync(
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        var parsed = HandoffArgParser.Parse(args);
        if (parsed.Rejection is not null)
        {
            _logger.LogWarning(
                "Handoff rejected by the grammar: {Reason} ({TokenCount} token(s))",
                parsed.Rejection,
                args.Count);
            return HandoffReceipt.GrammarRefused;
        }

        if (parsed.Selection is not { HasSelection: true })
        {
            return HandoffReceipt.NoSelection;
        }

        var receipt = await _applyRunner(args, ct);
        _logger.LogInformation(
            "Handoff settled: applied {Applied}, {Reason}",
            receipt.Applied,
            receipt.Reason);
        return receipt;
    }

    /// <summary>
    /// The dispatch loop: one server at a time awaits a connection, hands
    /// it to the bounded handler, disposes it, and starts over — until the
    /// pump's cancellation ends it. The first server instance arrives
    /// pre-created from <see cref="Start"/> (the synchronous pipe-exists
    /// contract); every later instance is created here, one per served
    /// connection.
    /// </summary>
    private async Task PumpAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        var server = first;
        while (true)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                // The pipe itself broke under it: the pump stands down
                // (future redirects will report the pipe unavailable) and
                // the app carries on regardless — the dispatch is never a
                // trading dependency.
                server.Dispose();
                _logger.LogError(ex, "The handoff pipe failed; the dispatch pump stands down");
                return;
            }

            try
            {
                await HandleConnectionAsync(server, ct);
            }
            finally
            {
                // The connected peer's instance is consumed by its one
                // exchange; the next start finds a freshly created one.
                server.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                server = CreateServerStream();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The handoff pipe failed to come up; the dispatch pump stands down");
                return;
            }
        }
    }

    /// <summary>Create one server instance of the dispatch pipe.</summary>
    private NamedPipeServerStream CreateServerStream() => new(
        _pipeName,
        PipeDirection.InOut,
        MaxServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    /// <summary>
    /// One connected peer: read its bounded frame, relay the vector, write
    /// the settled receipt back. Every failure here is that peer's
    /// exchange failing — the pump keeps serving, and an apply that died
    /// never wedges the dispatch.
    /// </summary>
    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using var framing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            framing.CancelAfter(TimeSpan.FromMilliseconds(PeerFrameTimeoutMs));

            var payloadArgs = await ReadPayloadAsync(server, framing.Token);
            var receipt = await ProcessAsync(payloadArgs, ct);
            await WriteReceiptAsync(server, receipt, ct);
        }
        catch (Exception ex)
        {
            // A silent, oversized, truncated or half-died peer — and any
            // apply failure inside the exchange — never poisons the pump:
            // the pipe keeps serving the next start.
            _logger.LogWarning(
                ex,
                "The handoff exchange failed; the pipe keeps serving");
        }
    }

    /// <summary>Read the framed payload ([4-byte length][request JSON]) and hand the vector over.</summary>
    private static async Task<IReadOnlyList<string>> ReadPayloadAsync(Stream pipe, CancellationToken ct)
    {
        var header = await ReadBytesAsync(pipe, FrameHeaderBytes, ct);
        var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
        if (length is < 0 or > HandoffPayload.MaxFramedBytes)
        {
            throw new IOException("the handoff request frame is out of the framing bound");
        }

        var body = await ReadBytesAsync(pipe, length, ct);
        var payload = JsonSerializer.Deserialize<HandoffPayload>(body, HandoffJson.Options);
        return payload?.Args ?? throw new IOException("the handoff request decoded to nothing");
    }

    /// <summary>Write the framed receipt ([4-byte length][reply JSON]).</summary>
    private static async Task WriteReceiptAsync(Stream pipe, HandoffReceipt receipt, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(receipt, HandoffJson.Options);
        if (body.Length > HandoffPayload.MaxFramedBytes - FrameHeaderBytes)
        {
            throw new IOException("the handoff receipt exceeds the framing bound");
        }

        var frame = new byte[FrameHeaderBytes + body.Length];
        frame[0] = (byte)body.Length;
        frame[1] = (byte)(body.Length >> 8);
        frame[2] = (byte)(body.Length >> 16);
        frame[3] = (byte)(body.Length >> 24);
        body.CopyTo(frame, FrameHeaderBytes);
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>Read exactly the demanded number of bytes (zero reads end the frame honestly).</summary>
    private static async Task<byte[]> ReadBytesAsync(Stream pipe, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
            {
                throw new IOException("the handoff pipe ended mid-frame");
            }

            offset += read;
        }

        return buffer;
    }
}
