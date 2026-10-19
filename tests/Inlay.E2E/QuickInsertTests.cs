using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Inlay.E2E.Harness;

namespace Inlay.E2E;

/// <summary>Notepad and the Edge test page, shared by the Quick Insert tests; each test starts its own Inlay.</summary>
public sealed class QuickInsertTargets : IDisposable
{
    public QuickInsertTargets()
    {
        try
        {
            Notepad = new NotepadTarget();
            Edge = new EdgePage();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal NotepadTarget? Notepad { get; }

    internal EdgePage? Edge { get; }

    public void Dispose()
    {
        Edge?.Dispose();
        Notepad?.Dispose();
    }
}

/// <summary>
/// One happy path per new Quick Insert feature (1.1): images, Insert as File path, Let me pick, the keyboard
/// shortcut, recents after a pause, creating a snippet from Quick Insert, symbols, and a custom trigger character.
/// Test images are generated here; the user's files are never used.
/// </summary>
[Collection(Desktop.Name)]
public sealed class QuickInsertTests(QuickInsertTargets targets) : IClassFixture<QuickInsertTargets>, IDisposable
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(4);

    private readonly string _images = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "inlay-e2e-images-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private InlayApp? _inlay;

    public void Dispose()
    {
        _inlay?.Dispose();
        try
        {
            Directory.Delete(_images, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InlayApp Start(Action<InlayApp>? configure = null)
    {
        _inlay = new InlayApp();
        configure?.Invoke(_inlay);
        _inlay.Start();
        return _inlay;
    }

    private NotepadTarget Notepad()
    {
        var n = targets.Notepad!;
        n.Focus();
        return n;
    }

    private static string Expect(NotepadTarget target, string expected) =>
        Wait.For(target.ReadText, t => t == expected, s_settle, $"{target.Name} to read \"{expected}\"");

    /// <summary>A generated 24-bit BMP (Inlay accepts BMP, PNG, JPG, GIF, WebP).</summary>
    private string Image(string name, int w, int h, byte seed, DateTime? lastWriteUtc = null)
    {
        var stride = ((w * 3) + 3) & ~3;
        var data = new byte[54 + (stride * h)];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), h);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), 24);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = 54 + (y * stride) + (x * 3);
                (data[o], data[o + 1], data[o + 2]) = ((byte)(x * 4), (byte)(y * 4), seed);
            }
        }

        var path = Path.Combine(_images, name);
        File.WriteAllBytes(path, data);
        if (lastWriteUtc is { } t)
            File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    private static JsonObject Entry(string name, string kind, string insertAs, Action<JsonObject>? more = null)
    {
        var e = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name, ["kind"] = kind, ["insertAs"] = insertAs };
        more?.Invoke(e);
        return e;
    }

    [Fact]
    public void Image_is_pasted_into_the_Edge_page()
    {
        var source = Image("lgtm.bmp", 64, 48, 200);
        Start(app => app.WithImages(Entry("lgtm", "Image", "Image", e => e["file"] = app.AddToLibrary(source))));
        var rich = targets.Edge!.Rich;
        rich.Focus();
        Keyboard.SelectAllAndDelete();

        Keyboard.Type(":lgtm");
        _inlay!.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);

        Wait.Until(() => targets.Edge!.Mirror("richImages") == "1", s_settle, "an image in the Edge editor");
    }

    [Fact]
    public void Insert_as_file_path_types_the_path_in_Notepad()
    {
        var source = Image("diagram.bmp", 32, 32, 90);
        string? library = null;
        Start(app =>
        {
            library = app.AddToLibrary(source);
            app.WithImages(Entry("diagram", "Image", "Path", e => e["file"] = library));
        });
        var notepad = Notepad();

        Keyboard.Type(":diagram");
        _inlay!.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);

        var expected = Path.Combine(_inlay.DataDirectory, "Images", library!);
        Assert.Equal(expected, Expect(notepad, expected));
    }

    [Fact]
    public void Let_me_pick_inserts_the_image_chosen_with_the_arrows()
    {
        var now = DateTime.UtcNow;
        Image("oldest.bmp", 16, 16, 10, now.AddMinutes(-30));
        var middle = Image("middle.bmp", 16, 16, 20, now.AddMinutes(-20));
        Image("newest.bmp", 16, 16, 30, now.AddMinutes(-10));
        Start(app => app.WithImages(Entry("shots", "Folder", "Path", e =>
        {
            e["folder"] = _images;
            e["mode"] = "Pick";
            e["pickCount"] = 3;
        })));
        Wait.Until(() => _inlay!.Log.Contains("images", StringComparison.OrdinalIgnoreCase), s_settle, "the folder scan");
        var notepad = Notepad();

        Keyboard.Type(":shots");
        _inlay!.WaitForPopup();
        Thread.Sleep(300); // the strip's thumbnails fill in; the choice doesn't depend on them
        Keyboard.Press(Keyboard.VK_RIGHT); // newest → middle
        Keyboard.Press(Keyboard.VK_RETURN);

        Assert.Equal(middle, Expect(notepad, middle));
    }

    [Fact]
    public void Keyboard_shortcut_opens_Quick_Insert_without_typing_into_the_app()
    {
        Start(app => app.Settings["quickInsertShortcut"] = "Win+Alt+F11");
        var notepad = Notepad();
        Keyboard.Type("x");
        Expect(notepad, "x");

        Keyboard.Chord(Keyboard.VK_LWIN, Keyboard.VK_MENU, Keyboard.VK_F11);
        _inlay!.WaitForPopup();
        Keyboard.Type("degree");
        Assert.Equal("x", notepad.ReadText()); // the query went to Quick Insert, not to Notepad
        Keyboard.Press(Keyboard.VK_RETURN);

        Assert.Equal("x°", Expect(notepad, "x°"));
    }

    [Fact]
    public void Recents_open_after_a_pause_with_a_history_icon_row()
    {
        Start(app =>
        {
            app.Settings["showRecentsOnPause"] = true;
            app.Settings["recentsDelayMs"] = 400;
            File.WriteAllText(Path.Combine(app.DataDirectory, "frecency.json"), $$"""
                { "schemaVersion": 1, "items": { "🔥": { "score": 3, "lastUsed": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}} } } }
                """);
        });
        var notepad = Notepad();

        Keyboard.Type(":");
        _inlay!.WaitForPopup(TimeSpan.FromSeconds(2));
        Keyboard.Press(Keyboard.VK_RETURN);

        Assert.Equal("🔥", Expect(notepad, "🔥"));
    }

    [Fact]
    public void Create_snippet_from_Quick_Insert_inserts_it_back_into_Notepad()
    {
        Start();
        var notepad = Notepad();
        var notepadWindow = Keyboard.Target;

        Keyboard.Type("Best, :zqxsig");
        _inlay!.WaitForPopup(); // "Create snippet :zqxsig" is the only row
        Keyboard.Press(Keyboard.VK_RETURN);

        // Settings opens with the New snippet dialog; its text box has the focus.
        var settings = Wait.For(() => Native.WindowsOf(_inlay.ProcessId).FirstOrDefault(h => Native.WindowText(h) == "Inlay" && Native.IsWindowVisible(h) && Native.GetForegroundWindow() == h), h => h != 0, TimeSpan.FromSeconds(10), "Settings in front");
        Thread.Sleep(500); // the dialog's open animation
        Keyboard.Target = settings;
        var guard = Keyboard.TargetGuard;
        Keyboard.TargetGuard = null;
        try
        {
            Keyboard.Type("Kind regards");
            Keyboard.Press(Keyboard.VK_RETURN, ctrl: true); // Ctrl+Enter saves from the text box
        }
        finally
        {
            Keyboard.Target = notepadWindow;
            Keyboard.TargetGuard = guard;
        }

        Assert.Equal("Best, Kind regards", Expect(notepad, "Best, Kind regards"));
        Assert.Contains("zqxsig", File.ReadAllText(Path.Combine(_inlay.DataDirectory, "snippets.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Symbol_deg_inserts_the_degree_sign()
    {
        Start();
        var notepad = Notepad();

        Keyboard.Type("21 :deg:");
        Assert.Equal("21 °", Expect(notepad, "21 °"));
    }

    [Fact]
    public void A_custom_trigger_shows_in_the_rows_and_works()
    {
        Start(app => app.Settings["triggerCharacter"] = ";");
        var notepad = Notepad();

        Keyboard.Type(";degre");
        _inlay!.WaitForPopup();
        var texts = Automation.Instance.FromHandle(_inlay.VisiblePopup()).FindAllDescendants().Select(e => e.Name).ToList();
        Assert.Contains(texts, t => t.StartsWith(";degree", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith(":degree", StringComparison.Ordinal));

        Keyboard.Type("e;"); // the closing form uses the same character
        Assert.Equal("°", Expect(notepad, "°"));
    }
}
