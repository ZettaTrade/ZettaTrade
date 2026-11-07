namespace ZettaTrade.AdapterKit.Tests.Harness;

using System.Net;
using System.Text;

/// <summary>
/// The kit's offline REST disguise: an <see cref="HttpMessageHandler"/> that
/// answers the capture pack's recorded canned responses by URL fragment —
/// the request never leaves the process, so the REST faces are exercised
/// with captured payloads and zero network. Unmapped paths answer 404, so an
/// adapter reaching for an unplanned endpoint fails loudly inside the kit.
/// </summary>
/// <param name="cannedRoutes">The path fragment → canned body map from the capture pack.</param>
public sealed class KitCannedRestHandler(IReadOnlyDictionary<string, string> cannedRoutes) : HttpMessageHandler
{
    /// <summary>HTTP response code the unmapped paths answer (a mapped-but-unknown request is a kit wiring error).</summary>
    private const HttpStatusCode UnmappedStatusCode = HttpStatusCode.NotFound;

    /// <summary>The canned responses' uniform content type (cold REST captures ride JSON).</summary>
    private const string CannedContentType = "application/json";

    /// <summary>
    /// The route lined up per request path: the FIRST matching canned
    /// fragment serves the response. The base class requires the task shape,
    /// so the fully-synchronous canned lookup wraps in <see cref="Task.FromResult"/>
    /// (the standing exception: the interface mandates it).
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.ToString() ?? string.Empty;
        var route = cannedRoutes.FirstOrDefault(pair => path.Contains(pair.Key, StringComparison.Ordinal));
        if (route.Key is null)
        {
            var unmapped = new HttpResponseMessage(UnmappedStatusCode)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $"The kit's canned REST has no route for {path}",
                    Encoding.UTF8,
                    "text/plain"),
            };
            return Task.FromResult(unmapped);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(route.Value, Encoding.UTF8, CannedContentType),
        };
        return Task.FromResult(response);
    }
}
