namespace ZettaTrade.Infrastructure.Tests.Notifications;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A loopback SMTP server standing in for the user's own mail server
/// (T-262's email harness, the T-032 loopback pattern): it binds an
/// ephemeral localhost port and walks one SMTP session per connection —
/// the greeting, the EHLO extension advertisement, the AUTH LOGIN
/// exchange (the stub's credentials are the good pair; the server can be
/// scripted to reject them), the envelope and the DATA payload —
/// capturing the whole session transcript and handing the queued message
/// to the test through a task. The stub speaks the minimal session
/// System.Net.Mail's SmtpClient drives: EHLO advertised AUTH LOGIN, then
/// MAIL FROM / RCPT TO / DATA / QUIT.
/// </summary>
internal sealed class LoopbackSmtpServer : IDisposable
{
    #region Constants

    /// <summary>The good username the stub accepts (sample-shaped, never a real credential).</summary>
    internal const string GoodUsername = "trade-bot@example.org";

    /// <summary>The good password the stub accepts (sample-shaped, never a real credential).</summary>
    internal const string GoodPassword = "stub-app-pw-0";

    #endregion Constants

    #region Fields

    private readonly TcpListener _listener;
    private readonly bool _rejectCredentials;
    private readonly TaskCompletionSource<string> _authed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sessionCount;

    /// <summary>Queue one delivered message: append it and complete every parked waiter it satisfies (the append order is the arrival order).</summary>
    private async Task QueueMessageAsync(SmtpMessage message)
    {
        TaskCompletionSource<SmtpMessage>[] ready;
        await _gate.WaitAsync();
        try
        {
            _messages.Add(message);
            var satisfied = new List<int>();
            foreach (var (index, waiter) in _messageWaiters)
            {
                if (index < _messages.Count)
                {
                    satisfied.Add(index);
                }
            }

            ready = new TaskCompletionSource<SmtpMessage>[satisfied.Count];
            for (var i = 0; i < satisfied.Count; i++)
            {
                ready[i] = _messageWaiters[satisfied[i]];
                _messageWaiters.Remove(satisfied[i]);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var waiter in ready)
        {
            waiter.TrySetResult(message);
        }
    }

    #endregion Fields

    #region Startup

    /// <summary>Start a server on a fresh ephemeral localhost port.</summary>
    /// <param name="rejectCredentials">Script the AUTH exchange to fail (the 535 path the degrade facts ride).</param>
    internal static LoopbackSmtpServer Start(bool rejectCredentials = false)
    {
        var server = new LoopbackSmtpServer(rejectCredentials);
        server.RunAcceptLoop();
        return server;
    }

    private LoopbackSmtpServer(bool rejectCredentials)
    {
        _rejectCredentials = rejectCredentials;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Host = "127.0.0.1";
        Port = endpoint.Port;
    }

    /// <summary>The loopback host the SmtpClient points at.</summary>
    internal string Host { get; }

    /// <summary>The ephemeral loopback port the SmtpClient points at.</summary>
    internal int Port { get; }

    /// <summary>How many SMTP sessions the stub accepted (the throttle and quiet-state facts' probe).</summary>
    internal int SessionCount => _sessionCount;

    /// <summary>Begin accepting sessions (the loop dies with the server's dispose; the loop holds every session task it walks).</summary>
    private void RunAcceptLoop()
    {
        _acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    // The session walk is total (its own catch records every
                    // fault), so a held task owes no further observation.
                    var session = WalkSessionAsync(client);
                    _sessions.Add(session);
                }
            }
            catch (Exception)
            {
                // The listener's dispose ends the loop — the stub is a test
                // harness, no logging, no restarts.
            }
        });
    }

    #endregion Startup

    #region Capture

    /// <summary>The message queue (the envelope + the raw DATA payload per delivered mail, in arrival order).</summary>
    private readonly List<SmtpMessage> _messages = [];

    /// <summary>The parked waiters, keyed by the message index they await.</summary>
    private readonly Dictionary<int, TaskCompletionSource<SmtpMessage>> _messageWaiters = [];

    /// <summary>The waiters' and the transcript's gate (the sessions and the tests arrive concurrently).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The accept-loop task itself (the loop dies with the listener; the stub owes nobody a join).</summary>
    private Task? _acceptLoop;

    /// <summary>The held session tasks (the accept loop is the only writer — one loop, sequential adds; a walk is total, so no observation is owed).</summary>
    private readonly List<Task> _sessions = [];

    /// <summary>The wire transcript (the hung-session evidence the bounded awaits dump).</summary>
    private readonly List<string> _transcript = [];

    /// <summary>Wait for the Nth delivered message (0-based): an already-delivered index answers at once, a future one parks until it lands.</summary>
    internal async Task<SmtpMessage> WaitForMessageAsync(int index)
    {
        TaskCompletionSource<SmtpMessage>? waiter;
        await _gate.WaitAsync();
        try
        {
            if (index < _messages.Count)
            {
                return _messages[index];
            }

            waiter = new TaskCompletionSource<SmtpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _messageWaiters[index] = waiter;
        }
        finally
        {
            // The park waits OUTSIDE the gate: a held semaphore would deadlock the very queue that feeds it.
            _gate.Release();
        }

        return await waiter.Task;
    }

    /// <summary>The AUTH LOGIN exchange the first session walked (the credential-material fact).</summary>
    internal Task<string> WaitForAuthAsync() => _authed.Task;

    /// <summary>
    /// The whole stub's wire transcript (one line per direction hop, plus
    /// the session faults): the hung-session facts' evidence — a bounded
    /// test await dumps it when a delivery never lands.
    /// </summary>
    internal async Task<string> DumpTranscriptAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return string.Join(Environment.NewLine, _transcript);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Record one transcript line (the sessions arrive concurrently).</summary>
    private async Task RecordAsync(string line)
    {
        await _gate.WaitAsync();
        try
        {
            _transcript.Add(line);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One queued message: the envelope addresses and the raw DATA payload.</summary>
    internal sealed record SmtpMessage(string From, string To, string DataPayload);

    #endregion Capture

    #region Session

    /// <summary>
    /// Walk one SMTP session to its end (a QUIT, a fault or the test's
    /// dispose). The walk is TOTAL: even the setup's own fault (a dispose
    /// race at the listener's stop) rides the transcript's catch below, so
    /// the accept loop's held session tasks never fault unobserved.
    /// </summary>
    private async Task WalkSessionAsync(System.Net.Sockets.TcpClient client)
    {
        Interlocked.Increment(ref _sessionCount);
        try
        {
            await WalkSessionCoreAsync(client);
        }
        catch (Exception ex)
        {
            // The transcript carries the fault: the bounded test awaits
            // dump it when a delivery never lands.
            await RecordAsync("SESSION FAULT: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>The session's own walk: the resources, the command loop, the AUTH exchange.</summary>
    private async Task WalkSessionCoreAsync(System.Net.Sockets.TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        string? from = null;
        string? to = null;
        var payload = new StringBuilder();
        var authRejected = false;

        try
        {
            await WriteAsync(writer, "220 loopback ESMTP stub ready");

            while (await reader.ReadLineAsync() is { } line)
            {
                await RecordAsync("C: " + line);

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(writer, "250-loopback greets the trade sink");
                    await WriteAsync(writer, "250-AUTH LOGIN");
                    await WriteAsync(writer, "250 OK");
                }
                else if (line.Equals("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
                {
                    // The two-step variant: the prompts carry the encoded halves.
                    await WriteAsync(writer, "334 VXNlcm5hbWU6"); // base64 "Username:"
                    var encodedUser = await reader.ReadLineAsync();
                    await RecordAsync("C: " + (encodedUser is null ? "<disconnected>" : "<base64 credentials>"));
                    await WriteAsync(writer, "334 UGFzc3dvcmQ6"); // base64 "Password:"
                    var encodedPassword = await reader.ReadLineAsync();
                    await RecordAsync("C: " + (encodedPassword is null ? "<disconnected>" : "<base64 credentials>"));
                    authRejected = !await FinishAuthAsync(writer, encodedUser, encodedPassword);
                }
                else if (line.StartsWith("AUTH LOGIN ", StringComparison.OrdinalIgnoreCase))
                {
                    // The initial-response variant (the shape System.Net.Mail
                    // actually sends): the username rides the command line,
                    // only the password gets its prompt.
                    var encodedUser = line.Substring("AUTH LOGIN ".Length).Trim();
                    await RecordAsync("C: <base64 credentials>");
                    await WriteAsync(writer, "334 UGFzc3dvcmQ6"); // base64 "Password:"
                    var encodedPassword = await reader.ReadLineAsync();
                    await RecordAsync("C: " + (encodedPassword is null ? "<disconnected>" : "<base64 credentials>"));
                    authRejected = !await FinishAuthAsync(writer, encodedUser, encodedPassword);
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                {
                    // A session whose AUTH was rejected relays nothing: the
                    // real server's shape (an AUTH failure reply alone does
                    // not stop System.Net.Mail — it keeps sending
                    // unauthenticated, so the refusal must ride the
                    // envelope commands).
                    if (authRejected)
                    {
                        await WriteAsync(writer, "530 5.7.0 Authentication required");
                    }
                    else
                    {
                        from = line;
                        await WriteAsync(writer, "250 2.1.0 Ok");
                    }
                }
                else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    if (authRejected)
                    {
                        await WriteAsync(writer, "530 5.7.0 Authentication required");
                    }
                    else
                    {
                        to = line;
                        await WriteAsync(writer, "250 2.1.0 Ok");
                    }
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    if (authRejected)
                    {
                        await WriteAsync(writer, "530 5.7.0 Authentication required");
                    }
                    else
                    {
                        await WriteAsync(writer, "354 End data with <CR><LF>.<CR><LF>");
                        payload.Clear();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        {
                            payload.AppendLine(dataLine);
                        }

                        await WriteAsync(writer, "250 2.0.0 Ok: queued as the stub's one message");
                        await QueueMessageAsync(new SmtpMessage(from ?? string.Empty, to ?? string.Empty, payload.ToString()));
                    }
                }
                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(writer, "221 2.0.0 Bye");
                    return;
                }
                else if (line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                {
                    // The plain stub never upgrades: a STARTTLS pick against it
                    // degrades at the client side, which is the honest failure
                    // the sink's own lane warns about.
                    await WriteAsync(writer, "454 4.7.0 TLS not available");
                }
                else
                {
                    await WriteAsync(writer, "500 5.5.2 unrecognized");
                }
            }

            await RecordAsync("S: <the client closed the session>");
        }
        catch (Exception ex)
        {
            // The transcript carries the fault: the bounded test awaits
            // dump it when a delivery never lands.
            await RecordAsync("SESSION FAULT: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// Complete one AUTH LOGIN exchange: decode the halves, accept the
    /// good pair (completing the auth probe with the decoded username) or
    /// script the 535 rejection (the degrade facts' path); any half that
    /// does not decode reads as empty and rides the rejection. Returns
    /// whether the session may relay (the session refuses the envelope
    /// commands after a rejection — an AUTH failure reply alone does not
    /// stop System.Net.Mail).
    /// </summary>
    private async Task<bool> FinishAuthAsync(StreamWriter writer, string? encodedUser, string? encodedPassword)
    {
        var username = Decode(encodedUser);
        var password = Decode(encodedPassword);

        if (!_rejectCredentials && username == GoodUsername && password == GoodPassword)
        {
            await WriteAsync(writer, "235 2.7.0 Authentication successful");
            _authed.TrySetResult(username);
            return true;
        }

        await WriteAsync(writer, "535 5.7.8 Authentication credentials rejected");
        return false;
    }

    /// <summary>Write one session line and record it (the transcript's server side).</summary>
    private async Task WriteAsync(StreamWriter writer, string line)
    {
        await writer.WriteLineAsync(line);
        await RecordAsync("S: " + line);
    }

    /// <summary>Decode one AUTH LOGIN line (the base64 credential material).</summary>
    private static string Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length % 4 != 0)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    #endregion Session

    #region IDisposable

    public void Dispose() => _listener.Stop();

    #endregion IDisposable
}
