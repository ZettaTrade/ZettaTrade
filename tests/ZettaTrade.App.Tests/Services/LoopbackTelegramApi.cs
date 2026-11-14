namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// A loopback HTTP server standing in for the Telegram Bot API (T-041's
/// e2e harness, the T-032 loopback pattern): it binds an ephemeral localhost
/// port, captures the sendMessage request (the request path — the bot token
/// rides it — plus the JSON body) and answers with the response a test
/// scripts (status code + body, or a scripted delay). Every
/// <see cref="ServeAsync"/> parks on exactly one request — the test starts a
/// send and awaits the server task around it.
/// </summary>
internal sealed class LoopbackTelegramApi : IDisposable
{
    #region Constants

    /// <summary>The fake bot token the tests send with (sample-shaped, never a real credential).</summary>
    internal const string FakeToken = "stub-token-0";

    #endregion Constants

    #region Fields

    private readonly HttpListener _listener;

    #endregion Fields

    #region Startup

    /// <summary>Start a server on a fresh ephemeral localhost port.</summary>
    internal static LoopbackTelegramApi Start()
    {
        // Ephemeral port discovery: bind port 0, read the assigned port, free
        // it for the HttpListener (the tiny race window is acceptable for a
        // test; a collision would surface immediately as a bind failure).
        var finder = new TcpListener(IPAddress.Loopback, 0);
        finder.Start();
        var port = ((IPEndPoint)finder.LocalEndpoint).Port;
        finder.Stop();

        return new LoopbackTelegramApi(port);
    }

    private LoopbackTelegramApi(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        BaseUrl = new Uri($"http://localhost:{port}");
    }

    /// <summary>The endpoint the client's base address points at.</summary>
    internal Uri BaseUrl { get; }

    #endregion Startup

    #region Serving

    /// <summary>
    /// Serve exactly one request: park until it arrives, then answer with the
    /// scripted status and body (a positive delay runs before the answer —
    /// the paced-response shape) and hand the captured request back.
    /// </summary>
    internal async Task<CapturedTelegramRequest> ServeAsync(int statusCode, string body, TimeSpan? delay = null)
    {
        var context = await _listener.GetContextAsync();

        if (delay is { } paced)
        {
            await Task.Delay(paced);
        }

        var request = await ReadBodyAsync(context.Request);

        var payload = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload);
        context.Response.Close();

        return request;
    }

    /// <summary>
    /// Serve exactly one request that never answers (the connection holds
    /// past the client's timeout): park until it arrives, keep the response
    /// open forever — the caller's dispose closes the listener and with it
    /// the parked request. The captured request returns immediately.
    /// </summary>
    internal async Task<CapturedTelegramRequest> ServeHangingAsync()
    {
        var context = await _listener.GetContextAsync();
        var request = await ReadBodyAsync(context.Request);

        // The response stays open: the test's HttpClient timeout fires while
        // the listener holds the connection (disposed with the server).
        return request;
    }

    private static async Task<CapturedTelegramRequest> ReadBodyAsync(HttpListenerRequest request)
    {
        var payload = new byte[request.ContentLength64];
        await request.InputStream.ReadExactlyAsync(payload);

        return new CapturedTelegramRequest(
            request.Url!.AbsolutePath,
            request.HttpMethod,
            Encoding.UTF8.GetString(payload),
            request.Headers["Content-Type"] ?? string.Empty);
    }

    #endregion Serving

    #region IDisposable

    public void Dispose() => _listener.Close();

    #endregion IDisposable

    /// <summary>The captured request facts (the token-bearing path + the JSON body as sent).</summary>
    internal sealed record CapturedTelegramRequest(string Path, string Method, string Body, string ContentType);
}
