using System.Runtime.InteropServices;
using Comet.Platform.Native;

namespace Comet.Platform.Context;

/// <summary>Apps that have a window open right now, for the "exclude an app" pickers.</summary>
internal static unsafe class RunningApps
{
    [ThreadStatic]
    private static HashSet<uint>? t_pids;

    /// <summary>
    /// Executable names ("notepad.exe") of processes with a visible top-level window, sorted. One pass over the
    /// windows (a few milliseconds), unlike asking every process for its main window. Call off the UI thread.
    /// </summary>
    public static List<string> WithWindows()
    {
        t_pids = [];
        User32.EnumWindows(&Collect, 0);
        var self = Kernel32.GetCurrentProcessId();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in t_pids)
        {
            if (pid == self)
                continue;
            var name = ProcessInfo.Name(pid);
            if (name.Length > 0)
                names.Add(name);
        }

        t_pids = null;
        return [.. names];
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
