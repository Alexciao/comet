using Comet.Core.AutoReplace;
using Comet.Platform.Hook;
using Comet.Platform.Native;

namespace Comet.Platform.Input;

/// <summary>
/// Builds the <c>INPUT</c> array for one insertion so it reaches the app as a single <c>SendInput</c> batch:
/// backspaces for the typed token, the text, caret moves for <c>{cursor}</c>, then the boundary key.
/// Virtual keys carry their scan codes (some apps read those rather than the virtual key).
/// </summary>
internal sealed class InputBatch
{
    private const uint MAPVK_VK_TO_VSC = 0;

    private readonly List<INPUT> _inputs = new(64);
    private bool _leftShiftReleased;
    private bool _rightShiftReleased;

    public int Count => _inputs.Count;

    public ReadOnlySpan<INPUT> Inputs => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_inputs);

    public InputBatch Key(ushort vk, nuint signature = InputSignature.Comet, bool extended = false)
    {
        var flags = extended ? User32.KEYEVENTF_EXTENDEDKEY : 0;
        var scan = (ushort)User32.MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC, 0);
        Add(vk, scan, flags, signature);
        Add(vk, scan, flags | User32.KEYEVENTF_KEYUP, signature);
        return this;
    }

    /// <summary>
    /// The user is usually still holding Shift from typing the closing ":" (Shift+period on many layouts) when an
    /// insertion starts, and their key-up is held until it finishes. Shift would turn caret moves into
    /// selections and Ctrl+V into Ctrl+Shift+V, so it is released for the batch and pressed again at the end.
    /// </summary>
    public InputBatch ReleaseShift()
    {
        _leftShiftReleased = (User32.GetAsyncKeyState(VirtualKey.VK_LSHIFT) & 0x8000) != 0;
        _rightShiftReleased = (User32.GetAsyncKeyState(VirtualKey.VK_RSHIFT) & 0x8000) != 0;
        if (_leftShiftReleased)
            Add(VirtualKey.VK_LSHIFT, 0x2A, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        if (_rightShiftReleased)
            Add(VirtualKey.VK_RSHIFT, 0x36, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

    /// <summary>Puts back a Shift released by <see cref="ReleaseShift"/> (the user's own key-up arrives later).</summary>
    public InputBatch RestoreShift()
    {
        if (_leftShiftReleased)
            Add(VirtualKey.VK_LSHIFT, 0x2A, 0, InputSignature.Comet);
        if (_rightShiftReleased)
            Add(VirtualKey.VK_RSHIFT, 0x36, 0, InputSignature.Comet);
        _leftShiftReleased = _rightShiftReleased = false;
        return this;
    }

    /// <summary>Left Shift down, Left: selects one character back. Shift stays down until <see cref="ReleaseLeftShift"/>.</summary>
    public InputBatch ShiftLeftHeld()
    {
        Add(VirtualKey.VK_LSHIFT, 0x2A, 0, InputSignature.Comet);
        Add(VirtualKey.VK_LEFT, 0x4B, User32.KEYEVENTF_EXTENDEDKEY, InputSignature.Comet);
        Add(VirtualKey.VK_LEFT, 0x4B, User32.KEYEVENTF_EXTENDEDKEY | User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

    public InputBatch ReleaseLeftShift()
    {
        Add(VirtualKey.VK_LSHIFT, 0x2A, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

    public InputBatch Backspaces(int count)
    {
        for (var i = 0; i < count; i++)
            Key(VirtualKey.VK_BACK);
        return this;
    }

    public InputBatch Lefts(int count)
    {
        for (var i = 0; i < count; i++)
            Key(VirtualKey.VK_LEFT, extended: true);
        return this;
    }

    /// <summary>
    /// Types text as UTF-16 units with <c>KEYEVENTF_UNICODE</c>. Surrogate pairs go as two consecutive units
    /// (Windows reassembles them into one WM_CHAR pair), so skin tones and ZWJ families arrive intact.
    /// Newlines and tabs are sent as real Enter/Tab keys, which every app understands.
    /// </summary>
    public InputBatch Text(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        continue;
                    Key(VirtualKey.VK_RETURN);
                    break;
                case '\n':
                    Key(VirtualKey.VK_RETURN);
                    break;
                case '\t':
                    Key(VirtualKey.VK_TAB);
                    break;
                default:
                    Add(0, c, User32.KEYEVENTF_UNICODE, InputSignature.Comet);
                    Add(0, c, User32.KEYEVENTF_UNICODE | User32.KEYEVENTF_KEYUP, InputSignature.Comet);
                    break;
            }
        }

        return this;
    }

    public InputBatch Boundary(Boundary boundary) => boundary.Kind switch
    {
        BoundaryKind.Enter => Key(VirtualKey.VK_RETURN),
        BoundaryKind.Tab => Key(VirtualKey.VK_TAB),
        _ => Text([boundary.Char]),
    };

    /// <summary>Ctrl down, V down, V up. Ctrl stays down until <see cref="ReleaseCtrl"/>.</summary>
    public InputBatch PressCtrlV()
    {
        var vScan = (ushort)User32.MapVirtualKeyExW(VirtualKey.VK_V, MAPVK_VK_TO_VSC, 0);
        Add(VirtualKey.VK_LCONTROL, LeftCtrlScan, 0, InputSignature.Comet);
        Add(VirtualKey.VK_V, vScan, 0, InputSignature.Comet);
        Add(VirtualKey.VK_V, vScan, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

    public InputBatch ReleaseCtrl()
    {
        Add(VirtualKey.VK_LCONTROL, LeftCtrlScan, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

    private const ushort LeftCtrlScan = 0x1D;

    /// <summary>Re-sends a key the user typed while an insertion was in flight.</summary>
    public InputBatch Replay(in KBDLLHOOKSTRUCT k, bool keyUp)
    {
        var flags = ((k.flags & User32.LLKHF_EXTENDED) != 0 ? User32.KEYEVENTF_EXTENDEDKEY : 0) | (keyUp ? User32.KEYEVENTF_KEYUP : 0);
        Add((ushort)k.vkCode, (ushort)k.scanCode, flags, InputSignature.Replay);
        return this;
    }

    public InputBatch Clear()
    {
        _inputs.Clear();
        return this;
    }

    private void Add(ushort vk, ushort scan, uint flags, nuint signature) =>
        _inputs.Add(new INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = signature } },
        });

    public unsafe uint Send()
    {
        if (_inputs.Count == 0)
            return 0;
        fixed (INPUT* p = Inputs)
            return User32.SendInput((uint)_inputs.Count, p, sizeof(INPUT));
    }
}
