using System.Runtime.InteropServices;
using Inlay.Platform.Native;

namespace Inlay.App.UI;

/// <summary>
/// A hidden top-level window on the UI thread. It receives messages posted from other threads (popup
/// updates from the hook thread), tray icon callbacks, and broadcasts that message-only windows never get:
/// <c>TaskbarCreated</c> (Explorer restarted), power, display and theme changes.
/// </summary>
internal sealed unsafe class MessageWindow : IDisposable
{
    private static readonly Dictionary<nint, MessageWindow> s_windows = [];

    private readonly Func<uint, nuint, nint, bool> _handler;

    /// <param name="handler">Returns true when the message was handled.</param>
    public MessageWindow(string className, Func<uint, nuint, nint, bool> handler)
    {
        _handler = handler;
        var instance = Kernel32.GetModuleHandleW(null);
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = name,
            };
            User32.RegisterClassExW(wc);
        }

        Handle = User32.CreateWindowExW(User32.WS_EX_TOOLWINDOW, className, "Inlay", User32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        s_windows[Handle] = this;
    }

    public nint Handle { get; }

    public bool Post(uint message, nuint wParam = 0, nint lParam = 0) => User32.PostMessageW(Handle, message, wParam, lParam);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (s_windows.TryGetValue(hwnd, out var window))
        {
            try
            {
                if (window._handler(msg, wParam, lParam))
                    return 0;
            }
            catch (Exception ex)
            {
                CrashHandler.OnUiException(ex);
            }
        }

        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        s_windows.Remove(Handle);
        User32.DestroyWindow(Handle);
    }
}
