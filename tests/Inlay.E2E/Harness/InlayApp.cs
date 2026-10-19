using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inlay.E2E.Harness;

/// <summary>
/// Runs Inlay against a scratch data folder (never the user's settings) with injected test input accepted.
/// Snippets and shorthands are written by the tests and removed with the folder.
/// </summary>
internal sealed class InlayApp : IDisposable
{
    private const uint WmQuit = Native.WM_APP + 101;

    private Process? _process;

    public InlayApp()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "inlay-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDirectory);
        // Recents and the shortcut are off unless a test turns them on: pauses between keys must not open Quick
        // Insert, and the user's own Inlay may hold the default shortcut.
        Settings = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["startAtLogin"] = false,
            ["quickInsertApps"] = new JsonArray(),
            ["showRecentsOnPause"] = false,
            ["quickInsertShortcut"] = "",
        };
        // Tests never reach GitHub: both weekly checks go to a closed local port unless a test serves them itself.
        Environment["INLAY_EMOJI_UPDATE_URL"] = "http://127.0.0.1:9/";
        Environment["INLAY_APP_UPDATE_URL"] = "http://127.0.0.1:9/latest";
    }

    public string DataDirectory { get; }

    public JsonObject Settings { get; }

    public static string ExePath =>
        System.Environment.GetEnvironmentVariable("INLAY_EXE") is { Length: > 0 } exe ? exe
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Inlay.App", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Inlay.exe"));

    /// <summary>True unless the app under test is the default Debug build (INLAY_EXE set to a published build).</summary>
    public static bool IsReleaseBuild => !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("INLAY_EXE"));

    public int ProcessId => _process?.Id ?? 0;

    public bool IsRunning => _process is { HasExited: false };

    public InlayApp WithFeatures(bool emoji = true, bool snippets = true, bool shorthands = true, bool images = true, bool symbols = true)
    {
        Settings["features"] = new JsonObject { ["emoji"] = emoji, ["symbols"] = symbols, ["snippets"] = snippets, ["images"] = images, ["shorthands"] = shorthands };
        return this;
    }

    /// <summary>images.json with the given entries (JSON objects in the settings format).</summary>
    public InlayApp WithImages(params JsonObject[] entries)
    {
        Write("images.json", new JsonObject { ["schemaVersion"] = 1, ["entries"] = new JsonArray([.. entries]) });
        return this;
    }

    /// <summary>A file in Inlay's image library (as "Add image" would put it there), returning its library name.</summary>
    public string AddToLibrary(string sourcePath)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant() + Path.GetExtension(sourcePath).ToLowerInvariant();
        var library = Path.Combine(DataDirectory, "Images");
        Directory.CreateDirectory(library);
        File.Copy(sourcePath, Path.Combine(library, name), overwrite: true);
        return name;
    }

    public InlayApp WithSnippets(params (string Name, string Text)[] snippets)
    {
        var list = new JsonArray();
        foreach (var (name, text) in snippets)
            list.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name, ["text"] = text });
        Write("snippets.json", new JsonObject { ["schemaVersion"] = 1, ["snippets"] = list });
        return this;
    }

    public InlayApp WithShorthands(params (string Shorthand, string Expansion)[] entries)
    {
        var list = new JsonArray();
        foreach (var (a, r) in entries)
            list.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["shorthand"] = a, ["expansion"] = r, ["matchCase"] = false, ["enabled"] = true });
        Write("shorthands.json", new JsonObject { ["schemaVersion"] = 2, ["entries"] = list });
        return this;
    }

    /// <summary>Extra environment variables for the Inlay process (e.g. INLAY_THEME for screenshots).</summary>
    public Dictionary<string, string> Environment { get; } = [];

    /// <summary>Opens Settings the way a second launch does.</summary>
    public void OpenSettings()
    {
        var window = Native.FindWindowW("Inlay.MessageWindow", null);
        if (window != 0)
            Native.PostMessageW(window, Native.WM_APP + 100, 0, 0);
    }

    public void Start()
    {
        Native.KeepDisplayOn();
        StopLeftovers();
        Write("settings.json", Settings);
        var startedBefore = StartedCount();
        var info = new ProcessStartInfo(ExePath, "--background --accept-injected-input") { UseShellExecute = false };
        info.Environment["INLAY_DATA_DIR"] = DataDirectory;
        foreach (var (name, value) in Environment)
            info.Environment[name] = value;
        _process = Process.Start(info) ?? throw new InvalidOperationException("Inlay did not start.");
        // Ready when the message window exists and the log says the hook decision was made.
        // A restarted instance appends to the same log: wait for a new "started:" line.
        Wait.Until(() => StartedCount() > startedBefore, TimeSpan.FromSeconds(20), "Inlay startup");
    }

    /// <summary>A test instance left behind by an aborted run (same executable as ours) would block single-instance start.</summary>
    private static void StopLeftovers()
    {
        foreach (var p in Process.GetProcessesByName("Inlay"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private int StartedCount() => File.Exists(LogPath) ? Log.Split("started:").Length - 1 : 0;

    public string LogPath => Path.Combine(DataDirectory, "logs", "inlay.log");

    /// <summary>Inlay's log so far (read with sharing: Inlay may be appending to it).</summary>
    public string Log
    {
        get
        {
            try
            {
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    /// <summary>Asks Inlay to quit cleanly (as the tray's Quit does) and waits for it.</summary>
    public void Stop()
    {
        if (_process is null || _process.HasExited)
            return;
        var window = Native.FindWindowW("Inlay.MessageWindow", null);
        if (window != 0)
            Native.PostMessageW(window, WmQuit, 0, 0);
        if (!_process.WaitForExit(5000))
            _process.Kill();
    }

    /// <summary>The suggestion popup, when it is on screen (visible and not cloaked).</summary>
    public nint VisiblePopup()
    {
        if (_process is null)
            return 0;
        foreach (var hwnd in Native.WindowsOf(_process.Id))
        {
            // Shown = visible, not cloaked, and on screen (the idle popup is parked far offscreen).
            if (Native.WindowText(hwnd) == "Inlay" && Native.IsWindowVisible(hwnd) && !Native.IsCloaked(hwnd) && Native.GetWindowRect(hwnd, out var r) && r.Right - r.Left > 100 && r.Left > -10000)
                return hwnd;
        }

        return 0;
    }

    public Native.RECT WaitForPopup(TimeSpan? timeout = null)
    {
        nint popup = 0;
        Wait.Until(() => (popup = VisiblePopup()) != 0, timeout ?? TimeSpan.FromSeconds(3), "popup to appear");
        Native.GetWindowRect(popup, out var r);
        return r;
    }

    public void WaitForPopupClosed() => Wait.Until(() => VisiblePopup() == 0, TimeSpan.FromSeconds(3), "popup to close");

    public void UpdateSettings(Action<JsonObject> change)
    {
        change(Settings);
        Write("settings.json", Settings);
    }

    private void Write(string file, JsonNode node) =>
        File.WriteAllText(Path.Combine(DataDirectory, file), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        try
        {
            // Keep the last run's log for diagnosis (it records events and timings, never typed text).
            if (File.Exists(LogPath))
            {
                File.Copy(LogPath, Path.Combine(Path.GetTempPath(), "inlay-e2e-last.log"), overwrite: true);
                var logs = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "inlay-e2e-logs"));
                File.Copy(LogPath, Path.Combine(logs.FullName, $"{DateTime.Now:HHmmss}-{Path.GetFileName(DataDirectory)}.log"), overwrite: true);
            }
        }
        catch (IOException)
        {
        }

        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Wait
{
    /// <summary>Polls a condition every 10 ms until it holds (a wait for an observable state, not a fixed delay).</summary>
    public static void Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F1} s waiting for {what}.");
            Thread.Sleep(10);
        }
    }

    public static T For<T>(Func<T> read, Func<T, bool> accept, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        T value;
        while (!accept(value = read()))
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F1} s waiting for {what}; last value: {Escape(value)}");
            Thread.Sleep(10);
        }

        return value;
    }

    /// <summary>Shows non-ASCII characters as code points so failures are readable in any console.</summary>
    public static string Escape<T>(T value)
    {
        var s = value?.ToString() ?? "null";
        var sb = new System.Text.StringBuilder();
        foreach (var rune in s.EnumerateRunes())
            sb.Append(rune.Value is >= 0x20 and < 0x7F ? ((char)rune.Value).ToString() : $"<U+{rune.Value:X4}>");
        return sb.ToString();
    }
}
