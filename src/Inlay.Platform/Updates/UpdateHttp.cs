using System.Net;
using System.Net.Http.Headers;

namespace Inlay.Platform.Updates;

/// <summary>
/// Inlay's only network access: the daily emoji data check and the daily app update check, both through this
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
    /// <remarks>
    /// Runs synchronously on the calling thread and returns a completed task: the checks run on their own
    /// low-priority thread, and a synchronous request keeps the parsing that follows it there too, rather than on
    /// a normal-priority pool thread.
    /// </remarks>
    public static Task<(HttpStatusCode Status, byte[]? Body, string? ETag)> GetAsync(HttpClient http, Uri uri, string? etag, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (etag is { Length: > 0 } && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        using var response = http.Send(request, HttpCompletionOption.ResponseContentRead, cancel);
        var newTag = response.Headers.ETag?.ToString();
        if (response.StatusCode == HttpStatusCode.NotModified || !response.IsSuccessStatusCode)
            return Task.FromResult<(HttpStatusCode, byte[]?, string?)>((response.StatusCode, null, newTag));
        using var body = new MemoryStream();
        response.Content.ReadAsStream(cancel).CopyTo(body);
        return Task.FromResult<(HttpStatusCode, byte[]?, string?)>((response.StatusCode, body.ToArray(), newTag));
    }
}
