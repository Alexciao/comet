using System.Net;
using System.Net.Http.Headers;

namespace Inlay.Platform.Updates;

/// <summary>
/// Inlay's only network access: the weekly emoji data check and the weekly app update check, both through this
/// one client. Requests carry nothing but <c>User-Agent: Inlay/&lt;version&gt;</c> (and the ETag of the last answer).
/// An architecture test fails if <c>HttpClient</c> shows up anywhere else.
/// </summary>
internal static class UpdateHttp
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Inlay/{AppPaths.Version}");
        return client;
    }

    /// <summary>
    /// A conditional GET: sends <c>If-None-Match</c> when <paramref name="etag"/> is known. Returns the status, the
    /// body (null for 304 or an error status) and the answer's ETag.
    /// </summary>
    public static async Task<(HttpStatusCode Status, byte[]? Body, string? ETag)> GetAsync(HttpClient http, Uri uri, string? etag, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (etag is { Length: > 0 } && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var newTag = response.Headers.ETag?.ToString();
        if (response.StatusCode == HttpStatusCode.NotModified || !response.IsSuccessStatusCode)
            return (response.StatusCode, null, newTag);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false), newTag);
    }
}
