using Inlay.Core.Pausing;
using Inlay.Platform.Native;

namespace Inlay.App.UI;

/// <summary>
/// The notification-area icon: left-click opens Settings; the right-click menu has Settings, Pause ▸ (15 minutes,
/// an hour, until resumed) or Resume, the update notice when a newer Inlay exists, and Quit. The full-color icon
/// (the same on light and dark taskbars) is dimmed while paused; the tooltip says the status ("Inlay — Paused
/// until 17:45"). The icon is re-added when Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint WmTray = User32.WM_APP + 50;
    private const uint IconId = 1;
    private const int SM_CXSMICON = 49;
    private const uint MF_POPUP = 0x0010;
    private const int CmdSettings = 1;
    private const int CmdResume = 2;
    private const int CmdQuit = 3;
    private const int CmdUpdate = 4;
    private const int CmdPauseFifteenMinutes = 5;
    private const int CmdPauseOneHour = 6;
    private const int CmdPauseUntilResumed = 7;

    private readonly nint _hwnd;
    private nint _icon;
    private int _iconSize;
    private bool _iconPaused;
    private bool _added;
    private bool _paused;
    private string _status = "Active";

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

    /// <summary>Pause ▸ chosen: for this long, or until resumed when null.</summary>
    public event Action<TimeSpan?>? PauseRequested;

    public event Action? ResumeRequested;

    /// <summary>
    /// The old on/off pause event, raised only while nothing handles <see cref="PauseRequested"/> and
    /// <see cref="ResumeRequested"/>. Remove once AppHost uses those.
    /// </summary>
    public event Action<bool>? PauseChanged;

    public event Action? Quit;

    public event Action? OpenUpdatePage;

    /// <summary>Paused: the dimmed icon, and Resume instead of Pause ▸ in the menu.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Update();
        }
    }

    /// <summary>"Resume (paused until 17:45)" or "Resume": the menu item while paused.</summary>
    public string ResumeText { get; set; } = "Resume";

    /// <summary>"Active", "Paused until 17:45", "Paused" or "All features off", shown in the tooltip.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            Update();
        }
    }

    /// <summary>A newer version ("1.3.0") to offer in the menu, or null.</summary>
    public string? UpdateVersion { get; set; }

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
        // Delete first: harmless when Explorer really restarted (the icon is gone), and needed when the broadcast
        // came while the icon still exists, where NIM_ADD alone would fail.
        var data = Data();
        Shell32.Shell_NotifyIconW(Shell32.NIM_DELETE, ref data);
        _added = false;
        _iconSize = 0; // the taskbar may be on another monitor now: reload at its DPI
        Show();
        Platform.Log.Info($"taskbar recreated: tray icon {(_added ? "re-added" : "could not be added")}");
    }

    /// <summary>DPI, pause or status changed: reload the icon if needed and refresh the tooltip.</summary>
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
        var pause = (nint)0;
        try
        {
            User32.AppendMenuW(menu, User32.MF_STRING, CmdSettings, "Settings");
            if (_paused)
            {
                User32.AppendMenuW(menu, User32.MF_STRING, CmdResume, ResumeText);
            }
            else
            {
                pause = User32.CreatePopupMenu();
                User32.AppendMenuW(pause, User32.MF_STRING, CmdPauseFifteenMinutes, "For 15 minutes");
                User32.AppendMenuW(pause, User32.MF_STRING, CmdPauseOneHour, "For 1 hour");
                User32.AppendMenuW(pause, User32.MF_STRING, CmdPauseUntilResumed, "Until I resume");
                if (User32.AppendMenuW(menu, MF_POPUP, (nuint)pause, "Pause"))
                    pause = 0; // destroyed with the menu from now on
            }

            if (UpdateVersion is { } version)
                User32.AppendMenuW(menu, User32.MF_STRING, CmdUpdate, $"Update available ({version})");
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
                case CmdPauseFifteenMinutes:
                    RequestPause(PauseState.FifteenMinutes);
                    break;
                case CmdPauseOneHour:
                    RequestPause(PauseState.OneHour);
                    break;
                case CmdPauseUntilResumed:
                    RequestPause(null);
                    break;
                case CmdResume:
                    if (ResumeRequested is { } resume)
                        resume();
                    else
                        PauseChanged?.Invoke(false);
                    break;
                case CmdQuit:
                    Quit?.Invoke();
                    break;
                case CmdUpdate:
                    OpenUpdatePage?.Invoke();
                    break;
            }
        }
        finally
        {
            User32.DestroyMenu(menu);
            if (pause != 0)
                User32.DestroyMenu(pause);
        }
    }

    private void RequestPause(TimeSpan? duration)
    {
        if (PauseRequested is { } pause)
            pause(duration);
        else
            PauseChanged?.Invoke(true);
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
        var tip = $"Inlay — {_status}";
        tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(new Span<char>(data.szTip, 127));
        return data;
    }

    /// <summary>
    /// Loads Tray.ico (or the dimmed TrayPaused.ico) from the Assets folder next to the exe, at the small-icon size
    /// for the taskbar's DPI: Shell_NotifyIcon wants an icon drawn for that size, not one Windows scales, or it
    /// turns blurry at 125% and 150%. LoadIconWithScaleDown picks the closest larger image and scales it down.
    /// </summary>
    private void LoadIcon()
    {
        var size = User32.GetSystemMetricsForDpi(SM_CXSMICON, TaskbarDpi());
        if (_icon != 0 && size == _iconSize && _paused == _iconPaused)
            return;
        var name = _paused ? "TrayPaused.ico" : "Tray.ico";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        var hr = Comctl32.LoadIconWithScaleDown(0, path, size, size, out var icon);
        if (hr < 0)
        {
            // At the system's large-icon size (the taskbar at twice the DPI Windows started with) it looks for a
            // resource instead of the file; LoadImage reads the file's image of that size.
            icon = User32.LoadImageW(0, path, User32.IMAGE_ICON, size, size, User32.LR_LOADFROMFILE);
        }

        if (icon == 0)
        {
            Platform.Log.Warn($"tray icon {name} not loaded (0x{hr:X8})");
            return;
        }

        if (_icon != 0)
            User32.DestroyIcon(_icon);
        (_icon, _iconSize, _iconPaused) = (icon, size, _paused);
    }

    private static uint TaskbarDpi()
    {
        var taskbar = User32.FindWindowW("Shell_TrayWnd", null);
        var dpi = taskbar == 0 ? 0 : User32.GetDpiForWindow(taskbar);
        return dpi != 0 ? dpi : User32.GetDpiForSystem();
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
