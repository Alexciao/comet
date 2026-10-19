using System.Runtime.InteropServices;

namespace Inlay.E2E.Harness;

internal static unsafe partial class Native
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const int DWMWA_CLOAKED = 14;
    public const uint WM_APP = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short VkKeyScanExW(ushort ch, nint dwhkl);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

    [LibraryImport("user32.dll")]
    public static partial nint GetKeyboardLayout(uint idThread);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public static uint SendInputs(ReadOnlySpan<INPUT> inputs)
    {
        fixed (INPUT* p = inputs)
            return SendInput((uint)inputs.Length, p, sizeof(INPUT));
    }

    /// <summary>Top-level windows of a process.</summary>
    public static List<nint> WindowsOf(int pid)
    {
        s_pid = (uint)pid;
        s_found = [];
        EnumWindows(&Collect, 0);
        return s_found;
    }

    [ThreadStatic]
    private static uint s_pid;

    [ThreadStatic]
    private static List<nint>? s_found;

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == s_pid)
            s_found!.Add(hwnd);
        return 1;
    }

    public static string WindowText(nint hwnd)
    {
        var buffer = new char[256];
        var n = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(n, 0));
    }

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowRect(nint hwnd, int attr, out RECT value, int size);

    /// <summary>The window as drawn (DWM frame bounds: no invisible resize borders).</summary>
    public static System.Drawing.Rectangle FrameBounds(FlaUI.Core.AutomationElements.AutomationElement window)
    {
        var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
        return DwmGetWindowRect(hwnd, 9, out var r, sizeof(RECT)) == 0
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : window.BoundingRectangle;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    private static int s_displayKept;

    /// <summary>
    /// Keeps the display on while the tests run: with the screen off DWM doesn't compose, so nothing renders and
    /// popups never appear. A background thread holds the request for the life of the test process only.
    /// </summary>
    public static void KeepDisplayOn()
    {
        if (Interlocked.Exchange(ref s_displayKept, 1) == 1)
            return;
        new Thread(() =>
        {
            const uint Continuous = 0x80000000, SystemRequired = 0x1, DisplayRequired = 0x2;
            _ = SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired);
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true, Name = "keep display on" }.Start();

        // The request keeps a lit display on but doesn't light a sleeping one; input does. A zero-distance mouse
        // move wakes the monitors without moving the cursor or touching any window.
        SendInputs([new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } } }]);
        Thread.Sleep(3000); // let the monitors come back before anything waits on rendering
    }

    public static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && v != 0;
}
