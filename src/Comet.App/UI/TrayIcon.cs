using Comet.Platform.Native;
using Microsoft.Win32;

namespace Comet.App.UI;

/// <summary>
/// The notification-area icon: left-click opens Settings; the right-click menu has Settings, Pause/Resume and
/// Quit. The glyph follows the taskbar theme, and the icon is re-added when Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint WmTray = User32.WM_APP + 50;
    private const uint IconId = 1;
    private const int SM_CXSMICON = 49;
    private const int CmdSettings = 1;
    private const int CmdPause = 2;
    private const int CmdQuit = 3;

    private readonly nint _hwnd;
    private nint _icon;
    private bool _added;
    private bool _paused;

    public TrayIcon(nint messageWindow)
    {
        _hwnd = messageWindow;
        TaskbarCreatedMessage = User32.RegisterWindowMessageW("TaskbarCreated");
        // Let the Win32 tray menu follow the dark theme (AllowDark = 1).
        _ = UxTheme.SetPreferredAppMode(1);
        UxTheme.FlushMenuThemes();
    }

    /// <summary>Broadcast when Explorer (re)creates the taskbar.</summary>
    public uint TaskbarCreatedMessage { get; }

    public event Action? OpenSettings;

    public event Action<bool>? PauseChanged;

    public event Action? Quit;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Update();
        }
    }

    public void Show()
    {
        LoadIcon();
        var data = Data();
        _added = Shell32.Shell_NotifyIconW(Shell32.NIM_ADD, ref data);
        data.uVersion = Shell32.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIconW(Shell32.NIM_SETVERSION, ref data);
    }

    /// <summary>Re-adds the icon after an Explorer restart.</summary>
    public void Recreate()
    {
        _added = false;
        Show();
    }

    /// <summary>Taskbar theme or DPI changed: reload the glyph.</summary>
    public void Update()
    {
        if (!_added)
            return;
        LoadIcon();
        var data = Data();
        Shell32.Shell_NotifyIconW(Shell32.NIM_MODIFY, ref data);
    }

    /// <summary>Handles the tray callback message. Returns true when handled.</summary>
    public bool HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == TaskbarCreatedMessage)
        {
            Recreate();
            return true;
        }

        if (message != WmTray)
            return false;

        var evt = (uint)(lParam & 0xFFFF);
        switch (evt)
        {
            case Shell32.NIN_SELECT:
            case Shell32.NIN_KEYSELECT:
                OpenSettings?.Invoke();
                break;
            case User32.WM_CONTEXTMENU:
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                break;
        }

        return true;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = User32.CreatePopupMenu();
        try
        {
            User32.AppendMenuW(menu, User32.MF_STRING, CmdSettings, "Settings");
            User32.AppendMenuW(menu, User32.MF_STRING, CmdPause, _paused ? "Resume" : "Pause");
            User32.AppendMenuW(menu, User32.MF_SEPARATOR, 0, null);
            User32.AppendMenuW(menu, User32.MF_STRING, CmdQuit, "Quit");
            // The owner must be foreground or the menu won't close when clicking elsewhere.
            User32.SetForegroundWindow(_hwnd);
            var cmd = User32.TrackPopupMenuEx(menu, User32.TPM_RETURNCMD | User32.TPM_NONOTIFY | User32.TPM_RIGHTBUTTON | User32.TPM_BOTTOMALIGN, x, y, _hwnd, 0);
            User32.PostMessageW(_hwnd, User32.WM_NULL, 0, 0);
            switch (cmd)
            {
                case CmdSettings:
                    OpenSettings?.Invoke();
                    break;
                case CmdPause:
                    PauseChanged?.Invoke(!_paused);
                    break;
                case CmdQuit:
                    Quit?.Invoke();
                    break;
            }
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATAW Data()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP,
            uCallbackMessage = WmTray,
            hIcon = _icon,
        };
        var tip = _paused ? "Comet (paused)" : "Comet";
        tip.AsSpan().CopyTo(new Span<char>(data.szTip, 127));
        return data;
    }

    private void LoadIcon()
    {
        var size = User32.GetSystemMetricsForDpi(SM_CXSMICON, User32.GetDpiForSystem());
        var name = TaskbarIsLight() ? "TrayLight.ico" : "TrayDark.ico";
        var icon = IconFromResource(name, size);
        if (icon == 0)
            return;
        if (_icon != 0)
            User32.DestroyIcon(_icon);
        _icon = icon;
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    /// <summary>Picks the closest image (≥ size when available) from an embedded .ico and creates an HICON.</summary>
    private static nint IconFromResource(string name, int size)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return 0;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var count = BitConverter.ToInt16(bytes, 4);
        int best = -1, bestSize = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            var s = bytes[entry] == 0 ? 256 : bytes[entry];
            var better = best < 0 || (s >= size && (bestSize < size || s < bestSize)) || (bestSize < size && s > bestSize);
            if (better)
            {
                best = i;
                bestSize = s;
            }
        }

        if (best < 0)
            return 0;
        var e = 6 + (best * 16);
        var length = BitConverter.ToInt32(bytes, e + 8);
        var offset = BitConverter.ToInt32(bytes, e + 12);
        fixed (byte* p = &bytes[offset])
            return User32.CreateIconFromResourceEx(p, (uint)length, true, 0x00030000, size, size, 0);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell32.Shell_NotifyIconW(Shell32.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != 0)
            User32.DestroyIcon(_icon);
        _icon = 0;
    }
}
