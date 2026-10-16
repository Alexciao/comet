using System.Net;
using Comet.Core.Emoji;
using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>
/// Silent emoji data update end to end: Comet checks a local server, downloads a newer pack, and swaps it in only
/// once the popup is closed; afterwards the pack's new shortcode works.
/// </summary>
[Collection(Desktop.Name)]
public sealed class EmojiUpdateTests : IDisposable
{
    private readonly CometApp _comet = new();
    private readonly HttpListener _listener = new();
    private readonly ManualResetEventSlim _releasePack = new(false);
    private NotepadTarget? _notepad;
    private byte[] _pack = [];
    private byte[] _manifest = [];

    public EmojiUpdateTests()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Endpoint = $"http://localhost:{port}/emoji-data/";
        _listener.Prefixes.Add(Endpoint);
        _listener.Start();
        new Thread(Serve) { IsBackground = true }.Start();
    }

    private string Endpoint { get; }

    private int ManifestRequests;
    private int NotModifiedResponses;

    public void Dispose()
    {
        _releasePack.Set();
        _notepad?.Dispose();
        _comet.Dispose();
        _listener.Stop();
        _listener.Close();
        _releasePack.Dispose();
    }

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

            var response = context.Response;
            var name = context.Request.Url!.Segments[^1];
            if (name == "manifest.json")
            {
                Interlocked.Increment(ref ManifestRequests);
                if (context.Request.Headers["If-None-Match"] == "\"pack-1\"")
                {
                    Interlocked.Increment(ref NotModifiedResponses);
                    response.StatusCode = 304;
                }
                else
                {
                    response.Headers["ETag"] = "\"pack-1\"";
                    response.OutputStream.Write(_manifest);
                }
            }
            else if (name == "emoji-data.bin")
            {
                _releasePack.Wait(TimeSpan.FromSeconds(30)); // the test opens the popup first
                response.OutputStream.Write(_pack);
            }
            else
            {
                response.StatusCode = 404;
            }

            response.Close();
        }
    }

    /// <summary>A pack newer than the built-in data, as big, with 🦆 under the new shortcode "cometduck".</summary>
    private void PreparePack()
    {
        using var embedded = EmojiData.LoadEmbedded();
        var entries = new List<EmojiSourceEntry>
        {
            new("🦆", "comet duck", ["cometduck"], [], [], null, 1, 0, 0, false, true),
            new("😄", "grinning face with smiling eyes", ["smile"], [], [], null, 2, 1, 0, false, true),
        };
        for (var i = entries.Count; i < embedded.Count; i++)
            entries.Add(new EmojiSourceEntry(char.ConvertFromUtf32(0x1F300 + i), $"filler {i}", [$"filler_{i}"], [], [], null, 10 + i, i, 0, false, true));
        _pack = EmojiBlobWriter.Write(entries, embedded.DataVersion + 1);
        _manifest = EmojiPackManifest.Describe(_pack, "1.0.0").ToJson();
    }

    private int Count(string text) => _comet.Log.Split(text).Length - 1;

    [Fact]
    public void A_new_pack_is_swapped_in_only_after_the_popup_closes_and_its_emoji_work()
    {
        PreparePack();
        _comet.Environment["COMET_EMOJI_UPDATE_URL"] = Endpoint;
        _comet.Environment["COMET_EMOJI_UPDATE_NOW"] = "1";
        _comet.Start();
        _notepad = new NotepadTarget();
        _notepad.Focus();

        // Popup open while the pack arrives.
        Keyboard.Type(":smi");
        _comet.WaitForPopup();
        var applied = Count("search data updated");
        Wait.Until(() => ManifestRequests > 0, TimeSpan.FromSeconds(15), "the update check");
        _releasePack.Set();
        Wait.Until(() => _comet.Log.Contains("emoji update check: Updated", StringComparison.Ordinal), TimeSpan.FromSeconds(15), "the pack to be verified");
        Thread.Sleep(500);
        Assert.NotEqual(0, _comet.VisiblePopup());
        Assert.Equal(applied, Count("search data updated")); // not swapped under the open popup

        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();
        Wait.Until(() => Count("search data updated") > applied, TimeSpan.FromSeconds(5), "the new data to be applied");

        Keyboard.Type(" :cometduck:");
        Assert.Equal(":smi 🦆", Wait.For(_notepad.ReadText, t => t == ":smi 🦆", TimeSpan.FromSeconds(4), "the new shortcode to insert"));
        Assert.True(File.Exists(Path.Combine(_comet.DataDirectory, "emoji-data.bin")));

        // The next start uses the downloaded pack, and the next check is a 304.
        _comet.Stop();
        var stateFile = Path.Combine(_comet.DataDirectory, "emoji-update.json");
        File.WriteAllText(stateFile, File.ReadAllText(stateFile).Replace("\"lastChecked\"", "\"lastCheckedBefore\"", StringComparison.Ordinal));
        _comet.Start();
        Wait.Until(() => _comet.Log.Contains("emoji update check: NotModified", StringComparison.Ordinal), TimeSpan.FromSeconds(15), "the conditional check");
        Assert.Equal(1, NotModifiedResponses);
        Assert.Contains("font check:", _comet.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void With_updates_turned_off_nothing_is_requested()
    {
        PreparePack();
        _comet.Settings["updateEmojiAutomatically"] = false;
        _comet.Environment["COMET_EMOJI_UPDATE_URL"] = Endpoint;
        _comet.Environment["COMET_EMOJI_UPDATE_NOW"] = "1";
        _comet.Start();
        Thread.Sleep(3000); // asserting an absence: well past the 500 ms test delay
        Assert.Equal(0, ManifestRequests);
    }
}
