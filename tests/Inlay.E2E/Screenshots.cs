using Inlay.E2E.Harness;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;

namespace Inlay.E2E;

/// <summary>
/// The README screenshots, written to the folder in <c>INLAY_SCREENSHOTS</c> (skipped when unset). Both themes come
/// from <c>INLAY_THEME</c>, so the system setting is never changed.
/// </summary>
[Collection(Desktop.Name)]
public sealed class Screenshots : IDisposable
{
    private static readonly string? s_folder = Environment.GetEnvironmentVariable("INLAY_SCREENSHOTS");

    private readonly InlayApp _inlay = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "inlay-shots-" + Guid.NewGuid().ToString("N"));

    private EdgePage? _edge;

    public void Dispose()
    {
        _edge?.Dispose();
        _inlay.Dispose();
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private string Picture(string name, Color from, Color to)
    {
        Directory.CreateDirectory(_work);
        var path = Path.Combine(_work, name + ".png");
        using var bmp = new Bitmap(320, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 320, 200), from, to, 35f);
            g.FillRectangle(brush, 0, 0, 320, 200);
        }

        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    private static JsonObject Entry(string name, string file) => new()
    {
        ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name, ["kind"] = "Image", ["insertAs"] = "Image", ["file"] = file,
    };

    private void Start(string theme)
    {
        if (s_folder is null)
            Assert.Skip("Set INLAY_SCREENSHOTS to a folder to take screenshots.");
        Directory.CreateDirectory(s_folder);
        _inlay.WithSnippets(
                ("email", "alex@example.com"),
                ("address", "1 Example Street\nSpringfield 12345"),
                ("signature", "Best regards,\nAlex\n{date:d MMMM yyyy}"))
            .WithShorthands(("brb", "be right back"), ("btw", "by the way"), ("omw", "on my way"), ("ty", "thank you"))
            .WithImages(
                Entry("lgtm", _inlay.AddToLibrary(Picture("lgtm", Color.FromArgb(0x2E, 0xB8, 0x9A), Color.FromArgb(0x0E, 0x5E, 0x6B)))),
                Entry("logo", _inlay.AddToLibrary(Picture("logo", Color.FromArgb(0xF2, 0x9E, 0x4C), Color.FromArgb(0xC4, 0x3D, 0x6B)))),
                Entry("diagram", _inlay.AddToLibrary(Picture("diagram", Color.FromArgb(0x5B, 0x8D, 0xEF), Color.FromArgb(0x2A, 0x2F, 0x7E)))));
        _inlay.Environment["INLAY_THEME"] = theme;
        _inlay.Start();
    }

    private static void Save(Rectangle area, string name) =>
        Capture.Rectangle(area).ToFile(Path.Combine(s_folder!, name + ".png"));

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Quick_Insert(string theme)
    {
        Start(theme);
        Thread.Sleep(3000); // let the emoji and thumbnail caches warm up
        _edge = new EdgePage();
        var t = theme.ToLowerInvariant();
        var field = _edge.Textarea;
        field.Focus();

        Keyboard.Type("Send it to :em");
        Save(Around(_inlay.WaitForPopup(), field.Bounds), $"quick-insert-{t}");
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _inlay.WaitForPopupClosed();

        Keyboard.Type(" and :l");
        Keyboard.Type("g");
        Save(Around(_inlay.WaitForPopup(), field.Bounds), $"quick-insert-images-{t}");
        Keyboard.Press(Keyboard.VK_ESCAPE);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Settings(string theme)
    {
        Start(theme);
        _inlay.OpenSettings();
        var window = Automation.WaitForWindow(w => w.Name == "Inlay Settings", TimeSpan.FromSeconds(10), "Settings");
        Automation.Foreground(window);
        var t = theme.ToLowerInvariant();
        Thread.Sleep(800); // entrance animation and Mica
        Save(Native.FrameBounds(window), $"settings-general-{t}");

        foreach (var (page, file) in new[] { ("Quick Insert", "quick-insert"), ("Snippets", "snippets"), ("Images", "images"), ("Emoji", "emoji"), ("Shorthands", "shorthands") })
        {
            window.FindFirstDescendant(c => c.ByName(page).And(c.ByControlType(ControlType.ListItem)))!.Click();
            Thread.Sleep(700);
            Save(Native.FrameBounds(window), $"settings-{file}-{t}");
        }

        window.FindFirstDescendant(c => c.ByName("Snippets").And(c.ByControlType(ControlType.ListItem)))!.Click();
        Thread.Sleep(400);
        window.FindFirstDescendant(c => c.ByName("Add snippet"))!.Click();
        Thread.Sleep(700);
        Save(Native.FrameBounds(window), $"settings-snippet-dialog-{t}");
        Keyboard.Target = Native.GetForegroundWindow();
        Keyboard.Press(Keyboard.VK_ESCAPE);
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
