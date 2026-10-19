using System.Runtime.InteropServices;
using Inlay.Core.Input;

namespace Inlay.Platform.Input;

/// <summary>The outcome of registering a <see cref="Shortcut"/> with Windows.</summary>
public enum ShortcutRegistration
{
    /// <summary>Registered: pressing it sends <c>WM_HOTKEY</c>.</summary>
    Registered,

    /// <summary>The shortcut is off (or suspended), so nothing is registered.</summary>
    Off,

    /// <summary>The rules don't allow it (see <see cref="Shortcut.Problem"/>); not attempted.</summary>
    NotAllowed,

    /// <summary>Another app (or Windows) already registered this combination.</summary>
    InUse,

    /// <summary>Registration failed for another reason (logged).</summary>
    Failed,
}

/// <summary>
/// One global shortcut, registered with <c>RegisterHotKey</c> (<c>MOD_NOREPEAT</c>) on a window of the calling
/// thread; Windows posts <c>WM_HOTKEY</c> to that window when it is pressed (see <see cref="IsPressed"/>). All
/// members must be called on the thread that owns the window.
/// <para>
/// <c>RegisterHotKey</c> is first come, first served: when another app holds the combination the result is
/// <see cref="ShortcutRegistration.InUse"/>, and setting the same shortcut again retries.
/// </para>
/// </summary>
internal sealed partial class GlobalShortcut : IDisposable
{
    public const uint WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly nint _hwnd;
    private readonly int _id;
    private bool _suspended;
    private bool _disposed;

    /// <param name="hwnd">The window that receives <c>WM_HOTKEY</c> (created on the calling thread).</param>
    /// <param name="id">The hotkey id, 0x0000–0xBFFE; <see cref="Test"/> uses <paramref name="id"/> + 1.</param>
    public GlobalShortcut(nint hwnd, int id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hwnd);
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, 0xBFFE);
        _hwnd = hwnd;
        _id = id;
    }

    /// <summary>The shortcut asked for last (registered or not).</summary>
    public Shortcut Shortcut { get; private set; }

    /// <summary>The result of the last registration attempt.</summary>
    public ShortcutRegistration Status { get; private set; } = ShortcutRegistration.Off;

    public bool IsRegistered => Status == ShortcutRegistration.Registered;

    /// <summary>
    /// Unregisters while true (Settings is recording a new shortcut: its keys must reach the recorder, and the
    /// current combination must test as free). Setting it back to false registers <see cref="Shortcut"/> again.
    /// </summary>
    public bool IsSuspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value || _disposed)
                return;
            _suspended = value;
            if (value)
                Unregister();
            else
                Register();
        }
    }

    /// <summary>
    /// Replaces the registered shortcut (<see cref="Shortcut.None"/> turns it off). Setting the shortcut that is
    /// already registered does nothing; setting one that failed tries again.
    /// </summary>
    public ShortcutRegistration Set(Shortcut shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (shortcut == Shortcut && IsRegistered)
            return Status;
        Unregister();
        Shortcut = shortcut;
        return Register();
    }

    /// <summary>True when a window message is this shortcut being pressed.</summary>
    public bool IsPressed(uint message, nuint wParam) => message == WM_HOTKEY && wParam == (nuint)_id;

    /// <summary>
    /// Whether <paramref name="shortcut"/> could be registered now, without changing the registration: it is tried
    /// under a second id and released at once. The registered shortcut tests as <see cref="ShortcutRegistration.Registered"/>.
    /// </summary>
    public ShortcutRegistration Test(Shortcut shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (shortcut == Shortcut && IsRegistered)
            return ShortcutRegistration.Registered;
        var result = TryRegister(_hwnd, _id + 1, shortcut);
        if (result == ShortcutRegistration.Registered)
            Release(_hwnd, _id + 1);
        return result;
    }

    /// <summary>
    /// Registers <paramref name="shortcut"/> as hotkey <paramref name="id"/> of <paramref name="hwnd"/> (a window of the
    /// calling thread, or 0 for the thread's queue). The caller releases it with <see cref="Release"/>.
    /// </summary>
    public static ShortcutRegistration TryRegister(nint hwnd, int id, Shortcut shortcut)
    {
        if (shortcut.IsNone)
            return ShortcutRegistration.Off;
        if (shortcut.Problem is not null)
            return ShortcutRegistration.NotAllowed;
        if (NativeMethods.RegisterHotKey(hwnd, id, NativeModifiers(shortcut.Modifiers) | MOD_NOREPEAT, (uint)shortcut.Key))
            return ShortcutRegistration.Registered;
        var error = Marshal.GetLastPInvokeError();
        if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
            return ShortcutRegistration.InUse;
        Log.Warn($"RegisterHotKey({shortcut}) failed: {error}");
        return ShortcutRegistration.Failed;
    }

    /// <summary>Unregisters what <see cref="TryRegister"/> registered; false when nothing was registered.</summary>
    public static bool Release(nint hwnd, int id) => NativeMethods.UnregisterHotKey(hwnd, id);

    public void Dispose()
    {
        if (_disposed)
            return;
        Unregister();
        _disposed = true;
    }

    private ShortcutRegistration Register()
    {
        if (_suspended)
            return Status = ShortcutRegistration.Off;
        Status = TryRegister(_hwnd, _id, Shortcut);
        if (Status == ShortcutRegistration.Registered)
            Log.Info($"shortcut {Shortcut} registered");
        else if (Status == ShortcutRegistration.InUse)
            Log.Info($"shortcut {Shortcut} is in use by another app");
        return Status;
    }

    private void Unregister()
    {
        if (Status == ShortcutRegistration.Registered)
            Release(_hwnd, _id);
        Status = ShortcutRegistration.Off;
    }

    private static uint NativeModifiers(ShortcutModifiers m) =>
        (m.HasFlag(ShortcutModifiers.Alt) ? MOD_ALT : 0)
        | (m.HasFlag(ShortcutModifiers.Ctrl) ? MOD_CONTROL : 0)
        | (m.HasFlag(ShortcutModifiers.Shift) ? MOD_SHIFT : 0)
        | (m.HasFlag(ShortcutModifiers.Win) ? MOD_WIN : 0);

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterHotKey(nint hWnd, int id);
    }
}
