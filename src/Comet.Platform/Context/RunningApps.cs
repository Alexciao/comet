using System.Diagnostics;
using System.Runtime.InteropServices;
using Comet.Platform.Native;

namespace Comet.Platform.Context;

/// <summary>A program, as the Excluded apps lists show it.</summary>
/// <param name="FileName">Lowercase executable name ("notepad.exe"); what the exclusion lists store.</param>
/// <param name="Path">Full path of the executable, when known.</param>
/// <param name="DisplayName">The file's description ("Notepad"), or its name without ".exe".</param>
internal sealed record ProgramInfo(string FileName, string? Path, string DisplayName)
{
    /// <summary>Describes an executable from its path (reads its version resource; call off the UI thread).</summary>
    public static ProgramInfo FromPath(string path)
    {
        var file = System.IO.Path.GetFileName(path).ToLowerInvariant();
        string? description = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (FileNotFoundException)
        {
        }

        return new ProgramInfo(file, path, string.IsNullOrEmpty(description) ? Fallback(file) : description);
    }

    /// <summary>An app known only by its file name (not running, path unknown): "slack.exe" → "Slack".</summary>
    public static ProgramInfo FromFileName(string fileName) => new(fileName, null, Fallback(fileName));

    private static string Fallback(string file)
    {
        if (s_knownNames.TryGetValue(file, out var known))
            return known;
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        return name.Length == 0 ? file : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Names for the apps excluded by default, shown before (or without) their files being found.</summary>
    private static readonly Dictionary<string, string> s_knownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slack.exe"] = "Slack",
        ["discord.exe"] = "Discord",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["teams.exe"] = "Microsoft Teams (classic)",
        ["telegram.exe"] = "Telegram",
    };
}

/// <summary>Apps that have a window open right now, for the "Add app" dialog.</summary>
internal static unsafe class RunningApps
{
    [ThreadStatic]
    private static HashSet<uint>? t_pids;

    /// <summary>
    /// Programs with a visible top-level window, one entry per executable, sorted by display name. One pass over
    /// the windows (a few milliseconds), unlike asking every process for its main window. Call off the UI thread.
    /// </summary>
    public static List<ProgramInfo> WithWindows()
    {
        t_pids = [];
        User32.EnumWindows(&Collect, 0);
        var self = Kernel32.GetCurrentProcessId();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in t_pids)
        {
            if (pid == self)
                continue;
            var path = ProcessInfo.ImagePath(pid);
            if (path.Length > 0)
                paths.TryAdd(System.IO.Path.GetFileName(path), path);
        }

        t_pids = null;
        return [.. paths.Values.Select(ProgramInfo.FromPath).OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        if (User32.IsWindowVisible(hwnd) && (User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) == 0)
        {
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            t_pids!.Add(pid);
        }

        return 1;
    }
}
