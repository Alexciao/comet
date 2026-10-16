using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Comet.E2E.Harness;

/// <summary>A text surface in a real app that the tests type into and read back through UI Automation.</summary>
internal interface ITextTarget : IDisposable
{
    string Name { get; }

    /// <summary>Brings the field to the foreground with the caret in it, and empties it.</summary>
    void Focus();

    string ReadText();

    /// <summary>Screen position of the field, for checking the popup lands next to it.</summary>
    System.Drawing.Rectangle Bounds { get; }
}

internal static class Automation
{
    public static readonly UIA3Automation Instance = new();

    public static AutomationElement WaitForWindow(Func<AutomationElement, bool> match, TimeSpan timeout, string what)
    {
        var desktop = Instance.GetDesktop();
        AutomationElement? found = null;
        Wait.Until(() => (found = desktop.FindAllChildren().FirstOrDefault(w =>
        {
            try
            {
                return match(w);
            }
            catch (Exception)
            {
                return false;
            }
        })) is not null, timeout, what);
        return found!;
    }

    public static void Foreground(AutomationElement window)
    {
        var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
        window.SetForeground();
        Native.SetForegroundWindow(hwnd);
        Wait.Until(() => Native.GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(5), "target window to be in the foreground");
        Keyboard.Target = hwnd;
    }
}

/// <summary>Windows 11 Notepad editing a temp file (so the test never touches the user's own tabs).</summary>
internal sealed class NotepadTarget : ITextTarget
{
    private readonly string _file;
    private readonly AutomationElement _window;
    private readonly AutomationElement _document;

    public NotepadTarget()
    {
        _file = Path.Combine(Path.GetTempPath(), $"comet-e2e-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_file, "");
        var stem = Path.GetFileNameWithoutExtension(_file);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_file}\"") { UseShellExecute = true });
        _window = Automation.WaitForWindow(w => w.Name.Contains(stem, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15), "Notepad");
        _document = _window.FindFirstDescendant(c => c.ByControlType(ControlType.Document))
            ?? throw new InvalidOperationException("Notepad's document was not found.");
    }

    public string Name => "Notepad";

    public System.Drawing.Rectangle Bounds => _document.BoundingRectangle;

    public void Focus()
    {
        Automation.Foreground(_window);
        _document.Focus();
        Keyboard.SelectAllAndDelete();
    }

    public string ReadText() => _document.Patterns.Text.Pattern.DocumentRange.GetText(-1).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public void Dispose()
    {
        try
        {
            // Leave the tab clean and saved so closing it doesn't prompt, then close only this tab.
            Automation.Foreground(_window);
            Keyboard.SelectAllAndDelete();
            Keyboard.Press(0x53, ctrl: true); // Ctrl+S
            Keyboard.Press(0x57, ctrl: true); // Ctrl+W
        }
        catch (Exception)
        {
            // Best effort.
        }

        try
        {
            File.Delete(_file);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Microsoft Edge with a throwaway profile showing TestPage.html (textarea, contenteditable, password).</summary>
internal sealed class EdgePage : IDisposable
{
    private readonly Process _edge;
    private readonly string _profile;
    private readonly AutomationElement _window;

    public EdgePage()
    {
        _profile = Path.Combine(Path.GetTempPath(), "comet-e2e-edge-" + Guid.NewGuid().ToString("N")[..8]);
        var page = new Uri(Path.Combine(AppContext.BaseDirectory, "TestPage.html")).AbsoluteUri;
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        _edge = Process.Start(new ProcessStartInfo(edge,
            $"--user-data-dir=\"{_profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-features=msEdgeFRE,Translate --force-renderer-accessibility --new-window \"{page}\"")
        { UseShellExecute = false })!;
        _window = Automation.WaitForWindow(w => w.Name.StartsWith("Comet E2E", StringComparison.Ordinal), TimeSpan.FromSeconds(30), "Edge test page");
        Wait.Until(() => Field("Plain textarea") is not null, TimeSpan.FromSeconds(15), "page content");
    }

    public AutomationElement? Field(string label) =>
        _window.FindFirstDescendant(new PropertyCondition(Automation.Instance.PropertyLibrary.Element.Name, label));

    public ITextTarget Textarea => new EdgeField(this, "Plain textarea", "areaOut", "Edge textarea");

    public ITextTarget Rich => new EdgeField(this, "Rich editor", "richOut", "Edge contenteditable");

    public ITextTarget Password => new EdgeField(this, "Password", "secretOut", "Edge password");

    internal AutomationElement Window => _window;

    /// <summary>Reads a mirror span (the page copies field values into labelled spans as JSON strings).</summary>
    internal string Mirror(string id)
    {
        var span = _window.FindFirstDescendant(new PropertyCondition(Automation.Instance.PropertyLibrary.Element.Name, id));
        var text = span?.FindFirstChild()?.Name ?? span?.Name ?? "";
        // Find the text node sibling: the span's accessible name is its label, the content is its child text.
        return text;
    }

    public void Dispose()
    {
        try
        {
            if (!_edge.HasExited)
                _edge.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try
            {
                if (p.StartInfo.Arguments.Contains(_profile, StringComparison.Ordinal))
                    p.Kill();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            Thread.Sleep(500);
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class EdgeField(EdgePage page, string label, string mirror, string name) : ITextTarget
    {
        public string Name => name;

        private AutomationElement Element => page.Field(label) ?? throw new InvalidOperationException($"{label} not found");

        public System.Drawing.Rectangle Bounds => Element.BoundingRectangle;

        public void Focus()
        {
            Automation.Foreground(page.Window);
            Element.Click();
            Keyboard.SelectAllAndDelete();
        }

        public string ReadText()
        {
            var e = Element;
            if (e.Patterns.Value.IsSupported)
                return e.Patterns.Value.Pattern.Value.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (e.Patterns.Text.IsSupported)
                return e.Patterns.Text.Pattern.DocumentRange.GetText(-1).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
            return page.Mirror(mirror);
        }

        public void Dispose()
        {
        }
    }
}
