using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Comet.E2E.Harness;

/// <summary>
/// Runs Comet against a scratch data folder (never the user's settings) with injected test input accepted.
/// Snippets and auto-replacements are written by the tests and removed with the folder.
/// </summary>
internal sealed class CometApp : IDisposable
{
    private const uint WmQuit = Native.WM_APP + 101;

    private Process? _process;

    public CometApp()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "comet-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDirectory);
        Settings = new JsonObject { ["schemaVersion"] = 1, ["startAtLogin"] = false, ["pickerExcludedApps"] = new JsonArray() };
    }

    public string DataDirectory { get; }

    public JsonObject Settings { get; }

    public static string ExePath =>
        Environment.GetEnvironmentVariable("COMET_EXE") is { Length: > 0 } exe ? exe
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Comet.App", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Comet.exe"));

    /// <summary>True unless the app under test is the default Debug build (COMET_EXE set to a published build).</summary>
    public static bool IsReleaseBuild => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMET_EXE"));

    public int ProcessId => _process?.Id ?? 0;

    public bool IsRunning => _process is { HasExited: false };

    public CometApp WithFeatures(bool emoji = true, bool snippets = true, bool autoReplace = true)
    {
        Settings["features"] = new JsonObject { ["emoji"] = emoji, ["snippets"] = snippets, ["autoReplace"] = autoReplace };
        return this;
    }

    public CometApp WithSnippets(params (string Name, string Text)[] snippets)
    {
        var list = new JsonArray();
        foreach (var (name, text) in snippets)
            list.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name, ["text"] = text });
        Write("snippets.json", new JsonObject { ["schemaVersion"] = 1, ["snippets"] = list });
        return this;
    }

    public CometApp WithAutoReplace(params (string Abbreviation, string Replacement)[] entries)
    {
        var list = new JsonArray();
        foreach (var (a, r) in entries)
            list.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["abbreviation"] = a, ["replacement"] = r, ["matchCase"] = false, ["enabled"] = true });
        Write("autoreplace.json", new JsonObject { ["schemaVersion"] = 1, ["entries"] = list });
        return this;
    }

    public void Start()
    {
        StopLeftovers();
        Write("settings.json", Settings);
        var info = new ProcessStartInfo(ExePath, "--background --accept-injected-input") { UseShellExecute = false };
        info.Environment["COMET_DATA_DIR"] = DataDirectory;
        _process = Process.Start(info) ?? throw new InvalidOperationException("Comet did not start.");
        // Ready when the message window exists and the log says the hook decision was made.
        Wait.Until(() => Log.Contains("started:", StringComparison.Ordinal), TimeSpan.FromSeconds(20), "Comet startup");
    }

    /// <summary>A test instance left behind by an aborted run (same executable as ours) would block single-instance start.</summary>
    private static void StopLeftovers()
    {
        foreach (var p in Process.GetProcessesByName("Comet"))
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

    public string LogPath => Path.Combine(DataDirectory, "logs", "comet.log");

    /// <summary>Comet's log so far (read with sharing: Comet may be appending to it).</summary>
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

    /// <summary>Asks Comet to quit cleanly (as the tray's Quit does) and waits for it.</summary>
    public void Stop()
    {
        if (_process is null || _process.HasExited)
            return;
        var window = Native.FindWindowW("Comet.MessageWindow", null);
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
            if (Native.WindowText(hwnd) == "Comet" && Native.IsWindowVisible(hwnd) && !Native.IsCloaked(hwnd) && Native.GetWindowRect(hwnd, out var r) && r.Right - r.Left > 100 && r.Left > -10000)
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
                File.Copy(LogPath, Path.Combine(Path.GetTempPath(), "comet-e2e-last.log"), overwrite: true);
                var logs = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "comet-e2e-logs"));
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
