namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;

/// <summary>
/// How often, and how late, a second start may open the handoff pipe
/// before the handoff is declared unavailable. The production schedule
/// covers the one window the pipe can legitimately stay closed — the
/// running instance's own cold boot — without ever straying past a prompt
/// exit; the empty schedule collapses to the single immediate attempt
/// (the tests drive it, so no fact ever waits on a wall clock).
/// </summary>
/// <param name="AttemptDelaysMs">The delay BEFORE each connect attempt (the first attempt always rides at zero).</param>
/// <param name="ConnectTimeoutMs">Each connect attempt's own bound.</param>
public sealed record HandoffForwardSchedule(IReadOnlyList<int> AttemptDelaysMs, int ConnectTimeoutMs)
{
    /// <summary>The shipped schedule: an immediate try, then two spaced retries across a cold boot.</summary>
    public static HandoffForwardSchedule Production { get; } =
        new([ColdStartRetryGraceMs, ColdStartRetryLateMs], PerAttemptConnectTimeoutMs);

    /// <summary>The first spaced retry (the running instance's normal walk is well inside it).</summary>
    private const int ColdStartRetryGraceMs = 700;

    /// <summary>The last spaced retry (covers slower boots; the total stays bounded and short).</summary>
    private const int ColdStartRetryLateMs = 2400;

    /// <summary>One connect attempt's bound (a listening pipe accepts inside it instantly).</summary>
    private const int PerAttemptConnectTimeoutMs = 700;
}

/// <summary>
/// What one forward attempt settled on, folded to the exit-code language
/// the shell sees.
/// </summary>
/// <param name="ExitCode">The mapped shell outcome.</param>
/// <param name="Detail">The file-log word of what happened (never user-facing).</param>
internal sealed record HandoffForwardResult(LaunchExitCode ExitCode, string Detail)
{
    public HandoffForwardResult(LaunchExitCode exitCode)
        : this(exitCode, exitCode.ToString())
    {
    }
}

/// <summary>
/// The second start's pipe client: one bounded, synchronous lane that
/// frames the raw vector down the handoff pipe, waits for the receiver's
/// settled receipt, and folds the outcome. The lane is deliberately
/// synchronous — the STAThread Main hosts no awaitable world yet, and the
/// lane must exit promptly — with every attempt bounded by the schedule
/// and no busy loops anywhere. It never writes to stdout; the dispatch
/// file log carries the story. A wedged reader cannot hold the lane past
/// the connect bound onto an unbounded read: the receiver is this same
/// ship's server, whose contract keeps framing reads bounded, so the
/// client's patience rides on that server contract instead of a kill
/// timer.
/// </summary>
internal static class HandoffForwarder
{
    /// <summary>Length of the frame's own header (a 4-byte little-endian prefix).</summary>
    private const int FrameHeaderBytes = 4;

    /// <summary>
    /// Forward the raw vector to the running instance through its handoff
    /// pipe. Every attempt connects under the schedule's bound; a connect
    /// that finds no listener (the still-booting claim holder, the deadlock
    /// never) is a silence, not an error, and the schedule may ride again.
    /// </summary>
    /// <param name="pipeName">The running instance's handoff pipe.</param>
    /// <param name="args">The raw vector, exactly as the shell handed it over.</param>
    /// <param name="schedule">The bounded attempt schedule.</param>
    /// <returns>The attempt's settled result (the gate folds it into the launch outcome).</returns>
    public static HandoffForwardResult Forward(
        string pipeName,
        IReadOnlyList<string> args,
        HandoffForwardSchedule schedule)
    {
        byte[] payloadBytes;
        try
        {
            payloadBytes = Encode(args);
        }
        catch (InvalidOperationException ex)
        {
            return new HandoffForwardResult(LaunchExitCode.ArgsRejected, ex.Message);
        }

        var attempts = schedule.AttemptDelaysMs.Count + 1;
        for (var attemptIndex = 0; attemptIndex < attempts; attemptIndex++)
        {
            if (attemptIndex < schedule.AttemptDelaysMs.Count)
            {
                Thread.Sleep(schedule.AttemptDelaysMs[attemptIndex]);
            }

            var outcome = TryForwardOnce(pipeName, payloadBytes, schedule.ConnectTimeoutMs);
            if (outcome is not null)
            {
                return outcome;
            }
        }

        return new HandoffForwardResult(
            LaunchExitCode.HandoffUnavailable,
            "the handoff pipe never served a forward within the bounded window");
    }

    /// <summary>One bounded attempt; null means "no listener — the schedule may try once more".</summary>
    private static HandoffForwardResult? TryForwardOnce(
        string pipeName,
        byte[] payloadBytes,
        int connectTimeoutMs)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(connectTimeoutMs);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return null;
        }

        try
        {
            WriteFrame(pipe, payloadBytes);
            var receipt = ReadFrame(pipe);
            return new HandoffForwardResult(LaunchExitCode.Forwarded, receipt.Reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            // The pipe accepted the connection but could not hold the
            // trade (a server dying between connect and reply, a peer that
            // answered with garbage): an attempted-but-unsettled forward
            // is not a clean redirect, and promptness wins over re-writing
            // the payload into a possibly dead pipe.
            return new HandoffForwardResult(
                LaunchExitCode.HandoffUnavailable,
                "the pipe broke after the connect: " + ex.GetType().Name);
        }
    }

    /// <summary>The raw vector's framed payload bytes ([4-byte length][request JSON]).</summary>
    private static byte[] Encode(IReadOnlyList<string> args)
    {
        // The SAME options the receiver decodes with: one wire naming
        // policy on both ends (the asymmetry serialized a PascalCase
        // payload into a camelCase decoder and every forward decoded to
        // nothing).
        var requestJson = JsonSerializer.SerializeToUtf8Bytes(new HandoffPayload(args), HandoffJson.Options);
        if (requestJson.Length > HandoffPayload.MaxFramedBytes - FrameHeaderBytes)
        {
            throw new InvalidOperationException(
                $"the handoff payload ({requestJson.Length} bytes) exceeds the framing bound ({HandoffPayload.MaxFramedBytes - FrameHeaderBytes})");
        }

        var frame = new byte[FrameHeaderBytes + requestJson.Length];
        frame[0] = (byte)requestJson.Length;
        frame[1] = (byte)(requestJson.Length >> 8);
        frame[2] = (byte)(requestJson.Length >> 16);
        frame[3] = (byte)(requestJson.Length >> 24);
        requestJson.CopyTo(frame, FrameHeaderBytes);
        return frame;
    }

    /// <summary>Write one full frame (the client's single write).</summary>
    private static void WriteFrame(Stream pipe, byte[] frame)
    {
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    /// <summary>Read and decode the framed reply ([4-byte length][reply JSON]).</summary>
    private static HandoffReceipt ReadFrame(Stream pipe)
    {
        var header = ReadBytes(pipe, FrameHeaderBytes);
        var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
        if (length is < 0 or > HandoffPayload.MaxFramedBytes)
        {
            throw new IOException("the handoff reply frame is out of the framing bound");
        }

        var reply = JsonSerializer.Deserialize<HandoffReceipt>(ReadBytes(pipe, length), HandoffJson.Options);
        return reply ?? throw new IOException("the handoff reply decoded to nothing");
    }

    /// <summary>Read exactly the demanded number of bytes (zero reads end the frame honestly).</summary>
    private static byte[] ReadBytes(Stream pipe, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = pipe.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new IOException("the handoff pipe ended mid-frame");
            }

            offset += read;
        }

        return buffer;
    }
}
