using Comet.Core.AutoReplace;
using Comet.Platform.Hook;
using Comet.Platform.Native;

namespace Comet.Platform.Input;

/// <summary>
/// Builds the <c>INPUT</c> array for one insertion so it reaches the app as a single <c>SendInput</c> batch:
/// backspaces for the typed token, the text, caret moves for <c>{cursor}</c>, then the boundary key.
/// </summary>
internal sealed class InputBatch
{
    private readonly List<INPUT> _inputs = new(64);

    public int Count => _inputs.Count;

    public ReadOnlySpan<INPUT> Inputs => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_inputs);

    public InputBatch Key(ushort vk, nuint signature = InputSignature.Comet, bool extended = false)
    {
        var flags = extended ? User32.KEYEVENTF_EXTENDEDKEY : 0;
        Add(vk, 0, flags, signature);
        Add(vk, 0, flags | User32.KEYEVENTF_KEYUP, signature);
        return this;
    }

    public InputBatch Backspaces(int count)
    {
        for (var i = 0; i < count; i++)
            Key(VirtualKey.VK_BACK);
        return this;
    }

    /// <summary>
    /// Selects the last <paramref name="count"/> characters (Shift+Left) so the following text replaces them.
    /// Used instead of backspaces before emoji: some apps (Windows 11 Notepad) lose the high surrogate of a
    /// VK_PACKET pair that arrives while they are still applying preceding deletions; a selection is not an
    /// edit, so the pair arrives intact. If the user is physically holding Shift, it is left alone.
    /// </summary>
    public InputBatch SelectBack(int count, bool shiftAlreadyDown)
    {
        if (count == 0)
            return this;
        if (!shiftAlreadyDown)
            Add(VirtualKey.VK_LSHIFT, 0x2A, 0, InputSignature.Comet);
        for (var i = 0; i < count; i++)
        {
            Add(VirtualKey.VK_LEFT, 0x4B, User32.KEYEVENTF_EXTENDEDKEY, InputSignature.Comet);
            Add(VirtualKey.VK_LEFT, 0x4B, User32.KEYEVENTF_EXTENDEDKEY | User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        }

        if (!shiftAlreadyDown)
            Add(VirtualKey.VK_LSHIFT, 0x2A, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
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
    /// (Windows reassembles them into one WM_CHAR pair), so skin tones, flags and ZWJ families arrive intact.
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

    public InputBatch Boundary(Boundary boundary)
    {
        return boundary.Kind switch
        {
            BoundaryKind.Enter => Key(VirtualKey.VK_RETURN),
            BoundaryKind.Tab => Key(VirtualKey.VK_TAB),
            _ => Text([boundary.Char]),
        };
    }

    public InputBatch Paste()
    {
        Add(VirtualKey.VK_CONTROL, 0, 0, InputSignature.Comet);
        Add(VirtualKey.VK_V, 0, 0, InputSignature.Comet);
        Add(VirtualKey.VK_V, 0, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        Add(VirtualKey.VK_CONTROL, 0, User32.KEYEVENTF_KEYUP, InputSignature.Comet);
        return this;
    }

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
