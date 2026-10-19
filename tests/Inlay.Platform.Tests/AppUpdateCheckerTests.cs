using System.Net;
using System.Text;
using Inlay.Core.Settings;
using Inlay.Platform.Updates;

namespace Inlay.Platform.Tests;

/// <summary>The daily "newer Inlay?" check against a local server that answers like GitHub's latest-release API.</summary>
public sealed class AppUpdateCheckerTests : IDisposable
{
    private readonly ReleaseServer _server = new();

    public void Dispose() => _server.Dispose();

    private AppUpdateChecker Checker(string current = "1.0.0") => new(UpdateHttp.Client, _server.Endpoint, new Version(current));

    private static string Release(string tag, bool prerelease = false, string url = "https://github.com/Alexciao/inlay/releases/tag/v1.3.0", string body = "- Notes", string published = "2026-10-20T08:30:00Z") =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}}, "body": "{{body}}", "published_at": "{{published}}", "assets": [] }""";

    [Fact]
    public async Task The_release_notes_are_kept_and_replaced_only_when_the_tag_changes()
    {
        var state = new AppUpdateState();
        var checker = Checker();
        _server.Set(Release("v1.3.0", body: "- First"), "\"r1\"");
        var (_, newer) = await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);
        Assert.Equal(("v1.3.0", "- First"), (state.LatestTag, state.LatestNotes));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), state.LatestPublished);
        Assert.Equal("- First", newer!.Notes);

        // The same release answered again (a new ETag): the notes stay as they were.
        _server.Set(Release("v1.3.0", body: "- Edited"), "\"r2\"");
        await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);
        Assert.Equal("- First", state.LatestNotes);

        // A 304 changes nothing.
        await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);
        Assert.Equal("\"r2\"", _server.LastIfNoneMatch);
        Assert.Equal("- First", state.LatestNotes);

        // A new release brings its own notes and date.
        _server.Set(Release("v1.4.0", body: "- Second", published: "2026-11-02T10:00:00Z"), "\"r3\"");
        (_, newer) = await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);
        Assert.Equal(("v1.4.0", "- Second"), (state.LatestTag, state.LatestNotes));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), newer!.Published);
    }

    [Fact]
    public async Task A_state_from_before_the_notes_asks_once_without_its_etag()
    {
        _server.Set(Release("v1.3.0", body: "- First"), "\"r1\"");
        var state = new AppUpdateState { ETag = "\"r1\"", LatestVersion = "1.3.0", LatestUrl = "https://github.com/Alexciao/inlay/releases/tag/v1.3.0" };

        var (outcome, newer) = await Checker().CheckAsync(state, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, outcome);
        Assert.Null(_server.LastIfNoneMatch);
        Assert.Equal("- First", newer!.Notes);
    }

    [Fact]
    public async Task A_newer_release_is_reported_and_remembered()
    {
        _server.Set(Release("v1.3.0"), "\"r1\"");
        var state = new AppUpdateState();

        var (outcome, newer) = await Checker().CheckAsync(state, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, outcome);
        Assert.Equal("1.3.0", newer!.DisplayVersion);
        Assert.Equal("https://github.com/Alexciao/inlay/releases/tag/v1.3.0", newer.Page.AbsoluteUri);
        Assert.Equal("\"r1\"", state.ETag);
        Assert.Equal("1.3.0", state.LatestVersion);
        Assert.NotNull(state.LastChecked);
    }

    [Fact]
    public async Task A_second_check_sends_the_etag_gets_304_and_still_knows_the_newer_version()
    {
        _server.Set(Release("v1.3.0"), "\"r1\"");
        var state = new AppUpdateState();
        var checker = Checker();
        await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);

        var (outcome, newer) = await checker.CheckAsync(state, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.NotModified, outcome);
        Assert.Equal("1.3.0", newer!.DisplayVersion);
        Assert.Equal("\"r1\"", _server.LastIfNoneMatch);
    }

    [Theory]
    [InlineData("v1.0.0")]   // the running version
    [InlineData("v0.9.5")]   // older
    public async Task The_same_or_an_older_release_is_not_offered(string tag)
    {
        _server.Set(Release(tag), "\"r\"");
        var (_, newer) = await Checker().CheckAsync(new AppUpdateState(), () => true, TestContext.Current.CancellationToken);
        Assert.Null(newer);
    }

    [Fact]
    public async Task Prereleases_and_pages_off_github_are_ignored()
    {
        _server.Set(Release("v2.0.0", prerelease: true), "\"a\"");
        Assert.Null((await Checker().CheckAsync(new AppUpdateState(), () => true, TestContext.Current.CancellationToken)).Newer);
        _server.Set(Release("v2.0.0", url: "https://example.com/inlay"), "\"b\"");
        Assert.Null((await Checker().CheckAsync(new AppUpdateState(), () => true, TestContext.Current.CancellationToken)).Newer);
    }

    [Fact]
    public async Task Only_the_user_agent_is_sent()
    {
        _server.Set(Release("v1.3.0"), "\"r1\"");
        await Checker().CheckAsync(new AppUpdateState(), () => true, TestContext.Current.CancellationToken);

        Assert.Equal($"Inlay/{AppPaths.Version}", _server.LastUserAgent);
        var extra = _server.LastHeaderNames.Except(["Host", "User-Agent", "Connection"], StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(extra.Count == 0, "unexpected headers: " + string.Join(", ", extra));
    }

    [Fact]
    public async Task Metered_or_offline_skips_without_a_request_and_failures_count_as_a_check()
    {
        var state = new AppUpdateState();
        var (outcome, _) = await Checker().CheckAsync(state, () => false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcome.Skipped, outcome);
        Assert.Equal(0, _server.Requests);
        Assert.Null(state.LastChecked);

        _server.Set(null, "");   // 404: no release yet
        (outcome, _) = await Checker().CheckAsync(state, () => true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.NotNull(state.LastChecked);
    }

    private sealed class ReleaseServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private volatile string? _body;
        private volatile string _etag = "";

        public ReleaseServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = new Uri($"http://localhost:{port}/repos/owner/inlay/releases/latest/");
            _listener.Prefixes.Add(Endpoint.AbsoluteUri);
            _listener.Start();
            new Thread(Serve) { IsBackground = true }.Start();
        }

        public Uri Endpoint { get; private set; } = null!;

        public int Requests;

        public string? LastIfNoneMatch { get; private set; }

        public string? LastUserAgent { get; private set; }

        public string[] LastHeaderNames { get; private set; } = [];

        public void Set(string? body, string etag) => (_body, _etag) = (body, etag);

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    return;
                }

                Interlocked.Increment(ref Requests);
                LastIfNoneMatch = context.Request.Headers["If-None-Match"];
                LastUserAgent = context.Request.UserAgent;
                LastHeaderNames = [.. context.Request.Headers.AllKeys.Where(k => k is not null && k != "If-None-Match").Select(k => k!)];
                var response = context.Response;
                if (_body is null)
                {
                    response.StatusCode = 404;
                }
                else if (LastIfNoneMatch == _etag)
                {
                    response.StatusCode = 304;
                }
                else
                {
                    response.Headers["ETag"] = _etag;
                    response.OutputStream.Write(Encoding.UTF8.GetBytes(_body));
                }

                response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
