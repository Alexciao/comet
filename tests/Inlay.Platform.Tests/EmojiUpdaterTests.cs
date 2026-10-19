using System.Net;
using Inlay.Core.Emoji;
using Inlay.Core.Search;
using Inlay.Core.Settings;
using Inlay.Platform.Updates;

namespace Inlay.Platform.Tests;

/// <summary>Update checks against a local HTTP server that serves a manifest and a pack like the GitHub release.</summary>
public sealed class EmojiUpdaterTests : IDisposable
{
    private static readonly Version s_app = new(1, 0, 0);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "inlay-updater-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PackServer _server = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private EmojiUpdater Updater() => new(_http, _server.Endpoint, _directory, s_app);

    /// <summary>A pack: 🦆 "inlayduck" (a shortcode no real dataset has) plus fillers.</summary>
    private static byte[] Pack(int dataVersion, int count = 100, bool withDuck = true)
    {
        var entries = new List<EmojiSourceEntry>();
        if (withDuck)
            entries.Add(new EmojiSourceEntry("🦆", "inlay duck", ["inlayduck"], [], [], null, 1, 0, 0, false, true));
        for (var i = entries.Count; i < count; i++)
            entries.Add(new EmojiSourceEntry(char.ConvertFromUtf32(0x1F300 + i), $"filler {i}", [$"filler_{i}"], [], [], null, 10 + i, i, 0, false, true));
        return EmojiBlobWriter.Write(entries, dataVersion);
    }

    private void Serve(byte[] pack, EmojiPackManifest? manifest = null, string etag = "\"v1\"") =>
        _server.Set(pack, (manifest ?? EmojiPackManifest.Describe(pack, "1.0.0")).ToJson(), etag);

    [Fact]
    public async Task A_newer_valid_pack_is_written_loaded_and_searchable()
    {
        Serve(Pack(300));
        var state = new EmojiUpdateState();
        var result = await Updater().CheckAsync(state, currentDataVersion: 200, currentEmojiCount: 100, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        using var data = result.Data!;
        Assert.Equal(300, data.DataVersion);
        Assert.Equal("\"v1\"", state.ETag);
        Assert.NotNull(state.LastChecked);
        Assert.True(File.Exists(Path.Combine(_directory, EmojiUpdater.PackFileName)));

        var search = new SearchEngine(data, new SnippetIndex([]));
        var exact = search.FindExact("inlayduck");
        Assert.NotNull(exact);
        Assert.Equal("🦆", data.Emoji(exact.Value.Index).ToString());
    }

    [Fact]
    public async Task With_a_matching_etag_the_server_answers_304_and_nothing_is_downloaded()
    {
        Serve(Pack(300));
        var state = new EmojiUpdateState { ETag = "\"v1\"" };
        var result = await Updater().CheckAsync(state, 200, 100, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.NotModified, result.Outcome);
        Assert.Equal(1, _server.Requests);
        Assert.Equal(0, _server.PackRequests);
        Assert.False(File.Exists(Path.Combine(_directory, EmojiUpdater.PackFileName)));
        Assert.NotNull(state.LastChecked);
    }

    [Fact]
    public async Task Data_that_is_not_newer_is_not_downloaded_and_the_etag_is_kept()
    {
        Serve(Pack(200));
        var state = new EmojiUpdateState();
        var result = await Updater().CheckAsync(state, 200, 100, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, _server.PackRequests);
        Assert.Equal("\"v1\"", state.ETag);
    }

    [Fact]
    public async Task A_pack_failing_its_hash_is_discarded()
    {
        var pack = Pack(300);
        var manifest = EmojiPackManifest.Describe(pack, "1.0.0");
        pack[^1] ^= 0xFF;
        Serve(pack, manifest);
        var state = new EmojiUpdateState();
        var result = await Updater().CheckAsync(state, 200, 100, () => true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Rejected, result.Outcome);
        Assert.Contains("HashMismatch", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, EmojiUpdater.PackFileName)));
        Assert.Null(state.ETag); // the next check looks again
    }

    [Fact]
    public async Task A_pack_losing_too_many_emoji_is_discarded()
    {
        Serve(Pack(300, count: 50));
        var result = await Updater().CheckAsync(new EmojiUpdateState(), 200, 100, () => true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcome.Rejected, result.Outcome);
        Assert.Contains("CountDrop", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_metered_or_offline_connection_nothing_is_sent()
    {
        Serve(Pack(300));
        var state = new EmojiUpdateState();
        var result = await Updater().CheckAsync(state, 200, 100, () => false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcome.Skipped, result.Outcome);
        Assert.Equal(0, _server.Requests);
        Assert.Null(state.LastChecked);
    }

    [Fact]
    public async Task An_unreachable_server_fails_quietly_and_counts_as_a_check()
    {
        var updater = new EmojiUpdater(_http, new Uri("http://127.0.0.1:9/"), _directory, s_app);
        var state = new EmojiUpdateState();
        var result = await updater.CheckAsync(state, 200, 100, () => true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.NotNull(state.LastChecked);
    }

    [Fact]
    public async Task A_second_update_keeps_the_previous_pack_as_a_backup()
    {
        Serve(Pack(300), etag: "\"a\"");
        (await Updater().CheckAsync(new EmojiUpdateState(), 200, 100, () => true, TestContext.Current.CancellationToken)).Data!.Dispose();
        Serve(Pack(400), etag: "\"b\"");
        (await Updater().CheckAsync(new EmojiUpdateState(), 300, 100, () => true, TestContext.Current.CancellationToken)).Data!.Dispose();

        Assert.True(EmojiData.TryReadHeader(File.ReadAllBytes(Path.Combine(_directory, EmojiUpdater.PackFileName)), out _, out var current));
        Assert.True(EmojiData.TryReadHeader(File.ReadAllBytes(Path.Combine(_directory, EmojiUpdater.PackFileName + ".bak")), out _, out var backup));
        Assert.Equal(400, current);
        Assert.Equal(300, backup);
        Assert.False(File.Exists(Path.Combine(_directory, EmojiUpdater.PackFileName + ".tmp")));
    }

    [Fact]
    public void At_startup_a_newer_download_is_used_and_a_corrupt_one_is_deleted()
    {
        using var embedded = EmojiData.LoadEmbedded();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, EmojiUpdater.PackFileName);

        File.WriteAllBytes(path, Pack(embedded.DataVersion + 1));
        using (var newer = EmojiUpdater.LoadStartupData(_directory))
            Assert.Equal(embedded.DataVersion + 1, newer.DataVersion);

        File.WriteAllBytes(path, Pack(embedded.DataVersion - 1));
        using (var older = EmojiUpdater.LoadStartupData(_directory))
            Assert.Equal(embedded.DataVersion, older.DataVersion);
        Assert.True(File.Exists(path)); // stale, not corrupt

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        using (var fallback = EmojiUpdater.LoadStartupData(_directory))
            Assert.Equal(embedded.DataVersion, fallback.DataVersion);
        Assert.False(File.Exists(path));
    }

    /// <summary>Serves <c>manifest.json</c> (with an ETag, honouring If-None-Match) and <c>emoji-data.bin</c>.</summary>
    private sealed class PackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Thread _thread;
        private volatile byte[] _pack = [];
        private volatile byte[] _manifest = [];
        private volatile string _etag = "";

        public PackServer()
        {
            var port = FreePort();
            Endpoint = new Uri($"http://localhost:{port}/emoji-data/");
            _listener.Prefixes.Add(Endpoint.AbsoluteUri);
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public Uri Endpoint { get; }

        public int Requests;
        public int PackRequests;

        public void Set(byte[] pack, byte[] manifest, string etag) => (_pack, _manifest, _etag) = (pack, manifest, etag);

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
                var response = context.Response;
                var name = context.Request.Url!.Segments[^1];
                if (name == EmojiUpdater.ManifestFileName)
                {
                    if (context.Request.Headers["If-None-Match"] == _etag)
                    {
                        response.StatusCode = 304;
                    }
                    else
                    {
                        response.Headers["ETag"] = _etag;
                        response.OutputStream.Write(_manifest);
                    }
                }
                else if (name == EmojiUpdater.PackFileName)
                {
                    Interlocked.Increment(ref PackRequests);
                    response.OutputStream.Write(_pack);
                }
                else
                {
                    response.StatusCode = 404;
                }

                response.Close();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
