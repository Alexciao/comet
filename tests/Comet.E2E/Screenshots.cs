using Comet.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;

namespace Comet.E2E;

/// <summary>
/// Screenshots for visual review, written to the folder in <c>COMET_SCREENSHOTS</c> (skipped when unset). Both
/// themes come from <c>COMET_THEME</c>, so the system setting is never changed.
/// </summary>
[Collection(Desktop.Name)]
public sealed class Screenshots : IDisposable
{
    private static readonly string? s_folder = Environment.GetEnvironmentVariable("COMET_SCREENSHOTS");

    private readonly CometApp _comet = new();

    private EdgePage? _edge;

    public void Dispose()
    {
        _edge?.Dispose();
        _comet.Dispose();
    }

    private void Start(string theme, bool withEntries = true)
    {
        if (s_folder is null)
            Assert.Skip("Set COMET_SCREENSHOTS to a folder to take screenshots.");
        Directory.CreateDirectory(s_folder);
        if (withEntries)
        {
            _comet.WithSnippets(("website", "https://example.com/"), ("addr", "Via Roma 1\nSpringfield 12345"), ("sig", "Best regards,\n— {date:d MMMM yyyy}"))
                .WithAutoReplace(("cmq", "comunque"), ("pk", "perché"), ("ty", "thank you"));
        }

        _comet.Environment["COMET_THEME"] = theme;
        _comet.Start();
    }

    private static void Save(Rectangle area, string name) =>
        Capture.Rectangle(area).ToFile(Path.Combine(s_folder!, name + ".png"));

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Settings(string theme)
    {
        Start(theme);
        _comet.OpenSettings();
        var window = Automation.WaitForWindow(w => w.Name == "Comet Settings", TimeSpan.FromSeconds(10), "Settings");
        Automation.Foreground(window);
        var t = theme.ToLowerInvariant();
        Thread.Sleep(800); // entrance animation and Mica
        Save(Native.FrameBounds(window), $"settings-general-{t}");

        foreach (var (page, file) in new[] { ("Snippets", "snippets"), ("Auto-replace", "autoreplace"), ("Excluded apps", "excluded"), ("About", "about") })
        {
            window.FindFirstDescendant(c => c.ByName(page).And(c.ByControlType(ControlType.ListItem)))!.Click();
            Thread.Sleep(600);
            Save(Native.FrameBounds(window), $"settings-{file}-{t}");
        }

        window.FindFirstDescendant(c => c.ByName("Snippets").And(c.ByControlType(ControlType.ListItem)))!.Click();
        Thread.Sleep(400);
        window.FindFirstDescendant(c => c.ByName("Add snippet"))!.Click();
        Thread.Sleep(600);
        Save(Native.FrameBounds(window), $"settings-snippet-dialog-{t}");
        Keyboard.Target = Native.GetForegroundWindow();
        Keyboard.Press(Keyboard.VK_ESCAPE);
    }

    /// <summary>A snippets file broken by a hand edit: Comet keeps a copy, starts with what it can, and says so.</summary>
    [Fact]
    public void Corrupt_file_notice()
    {
        Start("Light", withEntries: false);
        _comet.Stop();
        File.WriteAllText(Path.Combine(_comet.DataDirectory, "snippets.json"), "{ \"schemaVersion\": 1, \"snippets\": [ { \"name\": \"addr\", ");
        _comet.Start();
        _comet.OpenSettings();
        var window = Automation.WaitForWindow(w => w.Name == "Comet Settings", TimeSpan.FromSeconds(10), "Settings");
        Automation.Foreground(window);
        Thread.Sleep(800);
        Save(Native.FrameBounds(window), "settings-corrupt-file-light");
        Assert.Single(Directory.GetFiles(_comet.DataDirectory, "snippets.corrupt-*.json"));
        Assert.NotNull(window.FindFirstDescendant(c => c.ByControlType(ControlType.StatusBar).Or(c.ByClassName("InfoBar"))));
    }

    /// <summary>The lists ship empty: this is what the Snippets and Auto-replace pages look like at first.</summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Empty_lists(string theme)
    {
        Start(theme, withEntries: false);
        _comet.OpenSettings();
        var window = Automation.WaitForWindow(w => w.Name == "Comet Settings", TimeSpan.FromSeconds(10), "Settings");
        Automation.Foreground(window);
        foreach (var (page, file) in new[] { ("Snippets", "snippets"), ("Auto-replace", "autoreplace") })
        {
            window.FindFirstDescendant(c => c.ByName(page).And(c.ByControlType(ControlType.ListItem)))!.Click();
            Thread.Sleep(600);
            Save(Native.FrameBounds(window), $"settings-{file}-empty-{theme.ToLowerInvariant()}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Popup(string theme)
    {
        Start(theme);
        Wait.Until(() => _comet.Log.Contains("emoji bitmaps ready", StringComparison.Ordinal), TimeSpan.FromSeconds(60), "emoji warm-up");
        _edge = new EdgePage();
        var t = theme.ToLowerInvariant();
        var field = _edge.Textarea;
        field.Focus();

        Keyboard.Type("Ciao :smi");
        Save(Around(_comet.WaitForPopup(), field.Bounds), $"popup-smi-{t}");
        Keyboard.Press(Keyboard.VK_DOWN);
        Keyboard.Press(Keyboard.VK_DOWN);
        Thread.Sleep(150);
        Save(Around(_comet.WaitForPopup(), field.Bounds), $"popup-selection-{t}");
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();

        Keyboard.Type(" :web");
        Save(Around(_comet.WaitForPopup(), field.Bounds), $"popup-snippet-{t}");
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();

        Keyboard.Type(" :heart");
        Save(Around(_comet.WaitForPopup(), field.Bounds), $"popup-heart-{t}");
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();

        // Bottom-right corner of the primary screen: the popup must flip above the caret and stay on screen.
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var window = _edge.Window.AsWindow();
        window.Move(screen.Right - 260, screen.Bottom - 230);
        Thread.Sleep(500);
        field.Focus();
        Keyboard.Type(" :smi");
        var popup = _comet.WaitForPopup();
        Save(Rectangle.Intersect(screen, Around(popup, field.Bounds)), $"popup-edge-{t}");
        Assert.InRange(popup.Right, screen.Left, screen.Right);
        Assert.InRange(popup.Bottom, screen.Top, screen.Bottom);
        Keyboard.Press(Keyboard.VK_ESCAPE);
    }

    /// <summary>
    /// Flicker check: the popup's area is captured as fast as GDI allows while the popup opens, updates and closes;
    /// every distinct frame goes on a contact sheet. A frame with an empty, unstyled or half-drawn popup would show.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Popup_frames(string theme)
    {
        Start(theme);
        Wait.Until(() => _comet.Log.Contains("emoji bitmaps ready", StringComparison.Ordinal), TimeSpan.FromSeconds(60), "emoji warm-up");
        _edge = new EdgePage();
        var field = _edge.Textarea;
        field.Focus();
        Keyboard.Type(":hea");
        var area = Around(_comet.WaitForPopup(), field.Bounds);
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();
        Keyboard.Type(" ");

        var frames = new List<(long Ms, System.Drawing.Bitmap Image)>();
        var stop = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var capture = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
                frames.Add((clock.ElapsedMilliseconds, Capture.Rectangle(area).Bitmap));
        });
        capture.Start();
        Thread.Sleep(100);
        Keyboard.Type(":he");
        _comet.WaitForPopup();
        Keyboard.Type("a");
        Thread.Sleep(150);
        Keyboard.Press(Keyboard.VK_DOWN);
        Thread.Sleep(150);
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _comet.WaitForPopupClosed();
        Thread.Sleep(150);
        Volatile.Write(ref stop, true);
        capture.Join();

        var distinct = new List<(long Ms, System.Drawing.Bitmap Image)>();
        foreach (var f in frames)
        {
            if (distinct.Count == 0 || !SameImage(distinct[^1].Image, f.Image))
                distinct.Add(f);
        }

        Console.WriteLine($"{frames.Count} frames captured in {clock.ElapsedMilliseconds} ms, {distinct.Count} distinct");
        SaveSheet(distinct, $"frames-{theme.ToLowerInvariant()}");
    }

    private static bool SameImage(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        var rect = new System.Drawing.Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                return new ReadOnlySpan<byte>((void*)da.Scan0, da.Stride * a.Height).SequenceEqual(new ReadOnlySpan<byte>((void*)db.Scan0, db.Stride * b.Height));
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    private static void SaveSheet(List<(long Ms, System.Drawing.Bitmap Image)> frames, string name)
    {
        const int Columns = 4;
        var w = frames[0].Image.Width;
        var h = frames[0].Image.Height + 20;
        using var sheet = new System.Drawing.Bitmap(w * Columns, h * ((frames.Count + Columns - 1) / Columns));
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.Clear(System.Drawing.Color.Gray);
        using var font = new System.Drawing.Font("Segoe UI", 10);
        for (var i = 0; i < frames.Count; i++)
        {
            var (x, y) = (i % Columns * w, i / Columns * h);
            g.DrawString($"#{i}  t={frames[i].Ms} ms", font, System.Drawing.Brushes.White, x + 4, y + 2);
            g.DrawImage(frames[i].Image, x, y + 20);
        }

        sheet.Save(Path.Combine(s_folder!, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>The popup with some of the text field around it.</summary>
    private static Rectangle Around(Native.RECT popup, Rectangle field)
    {
        var area = Rectangle.FromLTRB(popup.Left, popup.Top, popup.Right, popup.Bottom);
        area = Rectangle.Union(area, Rectangle.FromLTRB(field.Left, Math.Max(field.Top, popup.Top - 120), field.Left + 40, Math.Min(field.Bottom, popup.Bottom)));
        area.Inflate(24, 24);
        return area;
    }
}
