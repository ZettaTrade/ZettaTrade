namespace ZettaTrade.App.Tests.Ai;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// A loopback HTTP server standing in for Ollama: it binds an ephemeral
/// localhost port, captures the chat request body, and answers with the
/// response a test scripts (status code + body). Every
/// <see cref="ServeAsync"/> parks on exactly one request — the test starts a
/// client call and awaits the server task around it.
/// </summary>
internal sealed class LoopbackChatServer : IDisposable
{
    #region Constants

    /// <summary>The chat path the provider posts to (Ollama's API).</summary>
    internal const string ChatPath = "/api/chat";

    #endregion

    #region Fields

    private readonly HttpListener _listener;

    #endregion

    #region Startup

    /// <summary>Start a server on a fresh ephemeral localhost port.</summary>
    internal static LoopbackChatServer Start()
    {
        // Ephemeral port discovery: bind port 0, read the assigned port, free
        // it for the HttpListener (the tiny race window is acceptable for a
        // test; a collision would surface immediately as a bind failure).
        var finder = new TcpListener(IPAddress.Loopback, 0);
        finder.Start();
        var port = ((IPEndPoint)finder.LocalEndpoint).Port;
        finder.Stop();

        return new LoopbackChatServer(port);
    }

    private LoopbackChatServer(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        BaseUrl = new Uri($"http://localhost:{port}");
    }

    /// <summary>The endpoint the provider's settings should point at.</summary>
    internal Uri BaseUrl { get; }

    #endregion

    #region Serving

    /// <summary>
    /// Serve exactly one request: park until it arrives, then answer with the
    /// scripted status and body and hand the captured request back.
    /// </summary>
    internal async Task<CapturedChatRequest> ServeAsync(int statusCode, string body)
    {
        var context = await _listener.GetContextAsync();
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
    /// Serve exactly one request with a paced chunked body: the first chunk
    /// is written and flushed, the writer then parks on the gate (the
    /// consumer observes the stream mid-flight — the streaming proof), and
    /// the captured request is handed back before the gate releases. Chunked
    /// transfer is explicit: without <c>SendChunked</c> HTTP.sys holds the
    /// whole response until the response closes (the Flush never reaches the
    /// socket), and the pacing would be impossible. The post-gate writes
    /// swallow the connection-abort faults (the abort fact cancels the
    /// consumer while the writer is parked) so the serve task always settles
    /// for the test to await it.
    /// </summary>
    internal async Task<CapturedChatRequest> ServePacedAsync(
        int statusCode,
        string first,
        Task gate,
        string? last = null)
    {
        var context = await _listener.GetContextAsync();
        var request = await ReadBodyAsync(context.Request);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.SendChunked = true;
        await WriteChunkAsync(context.Response, first);

        await gate;

        try
        {
            if (last is not null)
            {
                await WriteChunkAsync(context.Response, last);
            }

            context.Response.Close();
        }
        catch (Exception)
        {
            // The consumer went away while the writer was parked (the
            // abort shape): nothing to report — the serve task settles.
        }

        return request;
    }

    private static async Task WriteChunkAsync(HttpListenerResponse response, string chunk)
    {
        var payload = Encoding.UTF8.GetBytes(chunk);
        await response.OutputStream.WriteAsync(payload);
        await response.OutputStream.FlushAsync();
    }

    private static async Task<CapturedChatRequest> ReadBodyAsync(HttpListenerRequest request)
    {
        var payload = new byte[request.ContentLength64];
        await request.InputStream.ReadExactlyAsync(payload);

        return new CapturedChatRequest(
            request.Url!.AbsolutePath,
            Encoding.UTF8.GetString(payload));
    }

    /// <summary>The captured request facts (path + JSON body).</summary>
    internal sealed record CapturedChatRequest(string Path, string Body);

    #endregion

    #region IDisposable

    public void Dispose() => _listener.Close();

    #endregion
}
