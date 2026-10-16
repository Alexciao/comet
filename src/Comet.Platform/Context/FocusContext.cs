using Comet.Platform.Native;

namespace Comet.Platform.Context;

public enum Tristate : byte
{
    Unknown,
    No,
    Yes,
}

/// <summary>What has keyboard focus right now. Immutable; replaced on every focus change.</summary>
/// <param name="Generation">Increments on every focus change, so late async answers for an old focus are ignored.</param>
/// <param name="PacedEditor">The focused control when it needs paced input (Windows 11 Notepad's editor), else 0.</param>
internal sealed record FocusContext(int Generation, nint Foreground, nint Focus, uint ThreadId, uint ProcessId, string ProcessName, bool Elevated, Tristate Password, nint PacedEditor = 0)
{
    public static FocusContext None { get; } = new(0, 0, 0, 0, 0, "", false, Tristate.Unknown);

    /// <summary>Comet's own windows (Settings) — never act there.</summary>
    public bool IsSelf => ProcessId == Kernel32.GetCurrentProcessId();

    /// <summary>Safe to show the popup or expand: not elevated, not ours, confirmed not a password field.</summary>
    public bool IsSafe => !Elevated && !IsSelf && Password == Tristate.No;

    /// <summary>Reads the cheap facts about the focused window (a few microseconds; no cross-process calls that can hang).</summary>
    public static FocusContext Capture(int generation)
    {
        var (foreground, focus) = KeyboardFocus();
        if (foreground == 0)
            return None with { Generation = generation };

        var threadId = User32.GetWindowThreadProcessId(foreground, out var pid);
        if (focus != foreground)
        {
            // UWP/WinUI apps hosted by ApplicationFrameHost: the focused window belongs to the real app.
            threadId = User32.GetWindowThreadProcessId(focus, out var focusPid);
            if (focusPid != 0)
                pid = focusPid;
        }

        var name = ProcessInfo.Name(pid);
        var elevated = ProcessInfo.IsMoreElevatedThanUs(pid);
        var password = Win32Password(focus);
        var paced = WindowClass(focus) == "RichEditD2DPT" ? focus : 0;
        return new FocusContext(generation, foreground, focus, threadId, pid, name, elevated, password, paced);
    }

    /// <summary>The foreground window and the window holding keyboard focus in it (no cross-process calls).</summary>
    public static (nint Foreground, nint Focus) KeyboardFocus()
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground == 0)
            return (0, 0);
        var threadId = User32.GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        return (foreground, User32.GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != 0 ? info.hwndFocus : foreground);
    }

    /// <summary>Classic Edit controls expose ES_PASSWORD; for those we know the answer without UI Automation.</summary>
    private static Tristate Win32Password(nint hwnd)
    {
        var cls = WindowClass(hwnd);
        if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("RICHEDIT", StringComparison.Ordinal))
            return (User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE) & User32.ES_PASSWORD) != 0 ? Tristate.Yes : Tristate.No;
        return Tristate.Unknown;
    }

    [ThreadStatic]
    private static char[]? t_classBuffer;

    public static string WindowClass(nint hwnd)
    {
        var buffer = t_classBuffer ??= new char[256];
        var n = User32.GetClassNameW(hwnd, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }
}

internal static unsafe class ProcessInfo
{
    private static readonly int s_ourIntegrity = Integrity(Kernel32.GetCurrentProcessId()) ?? 0x2000;

    /// <summary>Executable file name in lowercase ("notepad.exe"), or "" when it can't be read.</summary>
    public static string Name(uint pid)
    {
        var handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0)
            return "";
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (!Kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                return "";
            var path = new ReadOnlySpan<char>(buffer, (int)size);
            var slash = path.LastIndexOf('\\');
            return path[(slash + 1)..].ToString().ToLowerInvariant();
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// True when the process runs at a higher integrity level than Comet (an elevated window). Windows blocks
    /// input injection into it anyway (UIPI), so Comet skips it quietly. Unreadable processes count as elevated.
    /// </summary>
    public static bool IsMoreElevatedThanUs(uint pid) => (Integrity(pid) ?? int.MaxValue) > s_ourIntegrity;

    private static int? Integrity(uint pid)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return null;
        try
        {
            if (!Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out var token))
                return null;
            try
            {
                byte* buffer = stackalloc byte[64];
                if (!Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, buffer, 64, out _))
                    return null;
                var sid = *(nint*)buffer; // TOKEN_MANDATORY_LABEL.Label.Sid
                var count = *(byte*)Advapi32.GetSidSubAuthorityCount(sid);
                return *(int*)Advapi32.GetSidSubAuthority(sid, (uint)(count - 1));
            }
            finally
            {
                Kernel32.CloseHandle(token);
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }
}
