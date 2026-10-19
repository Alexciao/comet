using System.Text;
using Inlay.Platform.Native;
using Inlay.Core.Input;
using static Inlay.Platform.Native.VirtualKey;

namespace Inlay.Platform.Hook;

[Flags]
internal enum Modifiers : byte
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
}

internal static class ModifierState
{
    /// <summary>
    /// Reads the held modifiers inside the low-level hook. The async key state is updated only after LL hooks
    /// run, so it is exact for modifiers pressed before the current key.
    /// </summary>
    public static Modifiers Read(uint hookFlags)
    {
        var m = Modifiers.None;
        if (Down(VK_SHIFT))
            m |= Modifiers.Shift;
        if (Down(VK_CONTROL))
            m |= Modifiers.Control;
        if (Down(VK_MENU) || (hookFlags & User32.LLKHF_ALTDOWN) != 0)
            m |= Modifiers.Alt;
        if (Down(VK_LWIN) || Down(VK_RWIN))
            m |= Modifiers.Windows;
        return m;
    }

    private static bool Down(int vk) => (User32.GetAsyncKeyState(vk) & 0x8000) != 0;
}

/// <summary>
/// Turns low-level key events into <see cref="KeyInput"/> using the keyboard layout of the foreground
/// window (Italian: <c>:</c> is Shift+period, <c>@</c> is AltGr+ò), never assuming US virtual-key codes.
/// <para>
/// <c>ToUnicodeEx</c> is called with flag 0x4 ("don't change keyboard state", Windows 10 1607+), so querying
/// a dead key here does not consume it and the target app still composes accents normally. Because that
/// also means we never see the composed character, dead keys are composed here (accent + base, NFC).
/// </para>
/// Runs on the hook thread only; no allocations except when composing a dead key.
/// </summary>
internal sealed unsafe class KeyTranslator
{
    private const uint DontChangeKeyboardState = 0x4;

    private readonly byte[] _state = new byte[256];
    private bool _capsLock;
    private bool _capsDown;
    private char _pendingDead;

    public KeyTranslator() => Resync();

    /// <summary>Re-reads toggle state (at hook install, after resume or unlock).</summary>
    public void Resync()
    {
        _capsLock = (User32.GetKeyState(VK_CAPITAL) & 1) != 0;
        _capsDown = false;
        _pendingDead = '\0';
    }

    /// <summary>A key went up (only Caps Lock matters: its next press toggles again).</summary>
    public void KeyUp(uint vk)
    {
        if (vk == VK_CAPITAL)
            _capsDown = false;
    }

    /// <summary>Translates a key-down. Writes 1–2 inputs (2 when a dead key could not compose) and returns the count.</summary>
    /// <param name="modifiers">Modifiers held when the key went down (see <see cref="ModifierState.Read"/>).</param>
    public int Translate(uint vk, uint scan, Modifiers modifiers, nint layout, Span<KeyInput> output)
    {
        if (vk == VK_CAPITAL)
        {
            // Windows toggles on the press, not on auto-repeat while the key is held.
            if (!_capsDown)
                _capsLock = !_capsLock;
            _capsDown = true;
            output[0] = KeyInput.Of(KeyKind.Ignored);
            return 1;
        }

        if (vk == VK_PACKET)
        {
            // Unicode injected by touch keyboards and IMEs: the character is in the scan code.
            output[0] = KeyInput.Text((char)(scan & 0xFFFF));
            return 1;
        }

        if (vk == VK_PROCESSKEY)
        {
            _pendingDead = '\0';
            output[0] = KeyInput.Of(KeyKind.Ime);
            return 1;
        }

        if (IsModifier(vk))
        {
            output[0] = KeyInput.Of(KeyKind.Ignored);
            return 1;
        }

        var shift = (modifiers & Modifiers.Shift) != 0;
        var ctrl = (modifiers & Modifiers.Control) != 0;
        var alt = (modifiers & Modifiers.Alt) != 0;
        var win = (modifiers & Modifiers.Windows) != 0;
        var altGr = ctrl && alt; // AltGr arrives as LCtrl+RAlt
        var shortcut = win || (ctrl ^ alt);

        KeyKind? special = vk switch
        {
            VK_BACK => KeyKind.Backspace,
            VK_RETURN => KeyKind.Enter,
            VK_TAB => KeyKind.Tab,
            VK_ESCAPE => KeyKind.Escape,
            VK_UP => KeyKind.Up,
            VK_DOWN => KeyKind.Down,
            // Insert: Shift+Insert pastes and Ctrl+Insert copies; either way the text is no longer what was typed.
            VK_LEFT or VK_RIGHT or VK_HOME or VK_END or VK_PRIOR or VK_NEXT or VK_DELETE or VK_INSERT => KeyKind.Navigation,
            _ => null,
        };

        if (special is { } kind)
        {
            _pendingDead = '\0';
            output[0] = shortcut || altGr ? KeyInput.Of(KeyKind.Shortcut) : KeyInput.Of(kind, shift);
            return 1;
        }

        if (shortcut)
        {
            _pendingDead = '\0';
            output[0] = KeyInput.Of(KeyKind.Shortcut);
            return 1;
        }

        Array.Clear(_state);
        if (shift)
            _state[VK_SHIFT] = _state[VK_LSHIFT] = 0x80;
        if (ctrl)
            _state[VK_CONTROL] = _state[VK_LCONTROL] = 0x80;
        if (alt)
            _state[VK_MENU] = _state[VK_RMENU] = 0x80;
        if (_capsLock)
            _state[VK_CAPITAL] = 0x01;

        char* buffer = stackalloc char[8];
        int n;
        fixed (byte* state = _state)
            n = User32.ToUnicodeEx(vk, scan, state, buffer, 8, DontChangeKeyboardState, layout);

        if (n < 0)
        {
            _pendingDead = buffer[0];
            output[0] = KeyInput.Of(KeyKind.DeadKey);
            return 1;
        }

        if (n == 0)
        {
            output[0] = KeyInput.Of(altGr ? KeyKind.Shortcut : KeyKind.Ignored);
            return 1;
        }

        var c = buffer[0];
        if (char.IsControl(c))
        {
            _pendingDead = '\0';
            output[0] = KeyInput.Of(KeyKind.Shortcut);
            return 1;
        }

        if (_pendingDead != '\0')
        {
            var dead = _pendingDead;
            _pendingDead = '\0';
            return Compose(dead, c, output);
        }

        output[0] = KeyInput.Text(c);
        if (n >= 2 && !char.IsControl(buffer[1]) && !char.IsHighSurrogate(c))
        {
            output[1] = KeyInput.Text(buffer[1]);
            return 2;
        }

        return 1;
    }

    /// <summary>Accent + base the way Windows composes it; when no composed form exists, both characters are typed.</summary>
    internal static int Compose(char dead, char baseChar, Span<KeyInput> output)
    {
        if (baseChar == ' ')
        {
            output[0] = KeyInput.Text(dead);
            return 1;
        }

        var combining = dead switch
        {
            '`' => '̀',
            '´' or '\'' => '́',
            '^' => '̂',
            '~' or '˜' => '̃',
            '¯' => '̄',
            '˘' => '̆',
            '˙' => '̇',
            '¨' or '"' => '̈',
            '˚' or '°' => '̊',
            '˝' => '̋',
            'ˇ' => '̌',
            '¸' => '̧',
            '˛' => '̨',
            _ => '\0',
        };

        if (combining != '\0')
        {
            var composed = string.Concat(baseChar.ToString(), combining.ToString()).Normalize(NormalizationForm.FormC);
            if (composed.Length == 1)
            {
                output[0] = KeyInput.Text(composed[0]);
                return 1;
            }
        }

        output[0] = KeyInput.Text(dead);
        output[1] = KeyInput.Text(baseChar);
        return 2;
    }

    private static bool IsModifier(uint vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL
        or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_NUMLOCK or VK_SCROLL or VK_PAUSE or VK_APPS;
}
