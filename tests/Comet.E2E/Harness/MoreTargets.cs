using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Comet.E2E.Harness;

/// <summary>Windows Terminal running PowerShell in a new window of its own; reads the current command line.</summary>
internal sealed class TerminalTarget : ITextTarget
{
    private readonly string _title = "CometE2E-" + Guid.NewGuid().ToString("N")[..8];
    private readonly AutomationElement _window;

    public TerminalTarget()
    {
        // Predictions are off so ghost text never appears in the line being read. (No ';' in the command: wt.exe
        // treats it as a separator between its own subcommands.)
        Process.Start(new ProcessStartInfo("wt.exe",
            $"-w new --title {_title} --suppressApplicationTitle powershell.exe -NoLogo -NoProfile -NoExit -Command \"Set-PSReadLineOption -PredictionSource None\"")
        { UseShellExecute = true });
        _window = Automation.WaitForWindow(w => w.Name.Contains(_title, StringComparison.Ordinal), TimeSpan.FromSeconds(20), "Windows Terminal");
        Wait.Until(() => Prompt() is not null, TimeSpan.FromSeconds(15), "the PowerShell prompt");
    }

    public string Name => "Windows Terminal";

    public System.Drawing.Rectangle Bounds => Control?.BoundingRectangle ?? _window.BoundingRectangle;

    private AutomationElement? Control => _window.FindFirstDescendant(c => c.ByClassName("TermControl"))
        ?? _window.FindFirstDescendant(c => c.ByControlType(ControlType.Document));

    public void Focus()
    {
        Automation.Foreground(_window);
        Keyboard.Press(Keyboard.VK_ESCAPE); // PSReadLine: clear the current line
    }

    /// <summary>The last prompt line, or null before PowerShell has started.</summary>
    private string? Prompt()
    {
        var text = Control?.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1);
        if (text is null)
            return null;
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').LastOrDefault(l => l.StartsWith("PS ", StringComparison.Ordinal));
        return line;
    }

    /// <summary>What has been typed after the prompt.</summary>
    public string ReadText()
    {
        var line = Prompt() ?? "";
        var start = line.IndexOf("> ", StringComparison.Ordinal);
        return start < 0 ? "" : line[(start + 2)..].TrimEnd();
    }

    public void Dispose()
    {
        try
        {
            Automation.Foreground(_window);
            Keyboard.Press(Keyboard.VK_ESCAPE);
            Keyboard.Type("exit\n");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Console.WriteLine($"Terminal cleanup: {ex.Message}");
        }
    }
}

/// <summary>VS Code with a throwaway profile editing a temp file; the text is read back from the saved file.</summary>
internal sealed class VsCodeTarget : ITextTarget
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "comet-e2e-code-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _file;
    private readonly AutomationElement _window;
    private readonly Process _process;

    public static string? Executable
    {
        get
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    public VsCodeTarget()
    {
        Directory.CreateDirectory(Path.Combine(_profile, "User"));
        // No suggestions or auto-closing (they would change what is typed), no welcome or sign-in dialog, no updates.
        File.WriteAllText(Path.Combine(_profile, "User", "settings.json"), """
            {
              "editor.quickSuggestions": { "other": false, "comments": false, "strings": false },
              "editor.suggestOnTriggerCharacters": false,
              "editor.wordBasedSuggestions": "off",
              "editor.autoClosingBrackets": "never",
              "editor.autoClosingQuotes": "never",
              "editor.accessibilitySupport": "on",
              "workbench.startupEditor": "none",
              "workbench.welcomePage.experimentalOnboarding": false,
              "chat.disableAIFeatures": true,
              "workbench.tips.enabled": false,
              "update.mode": "none",
              "telemetry.telemetryLevel": "off",
              "security.workspace.trust.enabled": false
            }
            """);
        _file = Path.Combine(_profile, $"comet-e2e-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_file, "");
        var start = new ProcessStartInfo(Executable!, $"--new-window --skip-welcome --skip-release-notes --disable-extensions --user-data-dir \"{_profile}\" \"{_file}\"") { UseShellExecute = false };
        // Inherited from an Electron host (such as an editor running the tests), this would start Code.exe as plain Node.
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        _process = Process.Start(start)!;
        var stem = Path.GetFileName(_file);
        _window = Automation.WaitForWindow(w => w.Name.Contains(stem, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(40), "VS Code");
    }

    public string Name => "VS Code";

    public System.Drawing.Rectangle Bounds => _window.BoundingRectangle;

    public void Focus()
    {
        Automation.Foreground(_window);
        Keyboard.SelectAllAndDelete();
    }

    /// <summary>Saves (Ctrl+S) and reads the file, so the check doesn't depend on the editor's accessibility tree.</summary>
    public string ReadText()
    {
        Keyboard.Press(0x53, ctrl: true);
        Thread.Sleep(150); // let VS Code write the file before reading it back (polled by the caller)
        try
        {
            return File.ReadAllText(_file).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        try
        {
            // A separate --user-data-dir makes this its own VS Code instance: its process tree is ours alone.
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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
}

/// <summary>Word with a new blank document, read through UI Automation, closed without saving.</summary>
internal sealed class WordTarget : ITextTarget
{
    private readonly Process _process;
    private readonly AutomationElement _window;

    public static string? Executable
    {
        get
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "root", "Office16", "WINWORD.EXE");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>
    /// Word keeps every document in one process; the test only runs when Word isn't open already, so closing the
    /// test's Word can never touch the user's documents.
    /// </summary>
    public static bool AlreadyRunning => Process.GetProcessesByName("WINWORD").Length > 0;

    public WordTarget()
    {
        _process = Process.Start(new ProcessStartInfo(Executable!, "/q /w") { UseShellExecute = false })!;
        _window = Automation.WaitForWindow(w => w.ClassName == "OpusApp", TimeSpan.FromSeconds(60), "Word");
        Wait.Until(() => Document is not null, TimeSpan.FromSeconds(30), "Word's document");
    }

    public string Name => "Word";

    private AutomationElement? Document => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Document));

    public System.Drawing.Rectangle Bounds => Document?.BoundingRectangle ?? _window.BoundingRectangle;

    public void Focus()
    {
        Automation.Foreground(_window);
        Document?.Focus();
        Keyboard.SelectAllAndDelete();
    }

    public string ReadText()
    {
        var text = Document?.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1) ?? "";
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(); // Word wasn't running before the test (see AlreadyRunning): only the unsaved test document
        }
        catch (InvalidOperationException)
        {
        }
    }
}
