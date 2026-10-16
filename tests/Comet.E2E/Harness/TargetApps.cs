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
        if (Native.GetForegroundWindow() != hwnd)
        {
            // Windows only lets a process take the foreground right after input; a lone Alt press satisfies that
            // (the standard workaround) without typing anything.
            Native.SendInputs([AltKey(false), AltKey(true)]);
            window.SetForeground();
            Native.SetForegroundWindow(hwnd);
        }

        Wait.Until(() => Native.GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(5), "target window to be in the foreground");
        Keyboard.Target = hwnd;
    }

    private static Native.INPUT AltKey(bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = 0x12, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } },
    };
}

/// <summary>
/// Windows 11 Notepad editing a temp file. Notepad restores the user's previous session, so the test file opens
/// as a tab next to the user's own (possibly unsaved) tabs: every keystroke is guarded to land only while the
/// test tab is the active one, and the test tab is closed only when it is saved and clean. Nothing is ever
/// discarded and Notepad is never killed.
/// </summary>
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

    private string Stem => Path.GetFileNameWithoutExtension(_file);

    /// <summary>The window title shows the active tab: ours?</summary>
    private bool OurTabActive() => _window.Name.Contains(Stem, StringComparison.OrdinalIgnoreCase);

    public void Focus()
    {
        Automation.Foreground(_window);
        if (!OurTabActive())
            throw new InvalidOperationException("The test tab is not the active Notepad tab.");
        Keyboard.TargetGuard = OurTabActive;
        _document.Focus();
        Keyboard.SelectAllAndDelete();
    }

    public string ReadText() => _document.Patterns.Text.Pattern.DocumentRange.GetText(-1).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// Empties and saves the test file, then closes its tab — only if that tab is active and clean, so closing
    /// can never prompt or touch another tab. On any doubt the tab is left open (harmless) rather than risked.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Automation.Foreground(_window);
            if (OurTabActive())
            {
                _document.Focus();
                Keyboard.SelectAllAndDelete();
                Keyboard.Press(0x53, ctrl: true); // Ctrl+S
                Wait.Until(() => !_window.Name.StartsWith('*'), TimeSpan.FromSeconds(3), "Notepad to save");
                if (OurTabActive() && !_window.Name.StartsWith('*'))
                {
                    Keyboard.Press(0x57, ctrl: true); // Ctrl+W
                    Wait.Until(() => !IsOpen(Stem), TimeSpan.FromSeconds(5), "Notepad tab to close");
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.WriteLine($"Notepad cleanup skipped: {ex.Message}");
        }
        finally
        {
            Keyboard.TargetGuard = null;
        }

        try
        {
            File.Delete(_file);
        }
        catch (IOException)
        {
        }
    }

    private static bool IsOpen(string stem) =>
        Automation.Instance.GetDesktop().FindAllChildren().Any(w => w.Name.Contains(stem, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// A field's exact value, from the read-only mirror input the page keeps up to date (JSON-encoded). Found by
    /// its label: Edge doesn't reliably expose the id of plain elements to UI Automation.
    /// </summary>
    internal string Mirror(string label)
    {
        var json = Field(label)?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? "";
        return json.Length == 0 ? "" : System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
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

        /// <summary>The field's text through UI Automation; password fields hide their value, so the page mirrors it.</summary>
        public string ReadText()
        {
            var e = Element;
            if (label == "Password")
                return page.Mirror(mirror);
            var text = e.Patterns.Value.IsSupported
                ? e.Patterns.Value.Pattern.Value.Value
                : e.Patterns.Text.Pattern.DocumentRange.GetText(-1).TrimEnd('\n');
            // contenteditable keeps a trailing space as a no-break space so it stays visible.
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(' ', ' ');
        }

        public void Dispose()
        {
        }
    }
}
