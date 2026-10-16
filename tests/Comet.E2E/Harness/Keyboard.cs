using static Comet.E2E.Harness.Native;

namespace Comet.E2E.Harness;

/// <summary>
/// Types like a physical keyboard: every character becomes the virtual key (plus Shift/AltGr) that produces it
/// on the foreground window's layout — on Italian, ":" is Shift+period. Comet's hook then translates them back
/// with the same layout, which is exactly the path real typing takes.
/// </summary>
internal static class Keyboard
{
    public const ushort VK_BACK = 0x08;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_UP = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_END = 0x23;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_A = 0x41;

    /// <summary>
    /// The window keystrokes are meant for. Every key checks that it is still in the foreground, so a test can
    /// never type into whatever else is on the desktop if focus moves away.
    /// </summary>
    public static nint Target { get; set; }

    /// <summary>Optional extra check run before every key (e.g. "the active Notepad tab is the test file").</summary>
    public static Func<bool>? TargetGuard { get; set; }

    private static void EnsureTarget()
    {
        if (Target == 0)
            throw new InvalidOperationException("Keyboard.Target is not set.");
        var fg = GetForegroundWindow();
        if (fg != Target)
            throw new InvalidOperationException($"Refusing to type: the foreground window ({fg}) is not the test window ({Target}).");
        if (TargetGuard is { } guard && !guard())
            throw new InvalidOperationException("Refusing to type: the test target is not active (guard failed).");
    }

    private static nint Layout()
    {
        var tid = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        return GetKeyboardLayout(tid);
    }

    /// <summary>Types text. "\n" presses Enter, "\t" Tab, "\b" Backspace.</summary>
    public static void Type(string text)
    {
        var layout = Layout();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    Press(VK_RETURN);
                    continue;
                case '\t':
                    Press(VK_TAB);
                    continue;
                case '\b':
                    Press(VK_BACK);
                    continue;
            }

            var scan = VkKeyScanExW(c, layout);
            if (scan == -1)
                throw new ArgumentException($"'{c}' can't be typed on the current keyboard layout.");
            var vk = (ushort)(scan & 0xFF);
            var shift = (scan & 0x100) != 0;
            var altGr = (scan & 0x600) == 0x600;
            var inputs = new List<INPUT>();
            if (altGr)
            {
                inputs.Add(Key(VK_LCONTROL, false, layout));
                inputs.Add(Key(VK_RMENU, false, layout, extended: true));
            }

            if (shift)
                inputs.Add(Key(VK_SHIFT, false, layout));
            inputs.Add(Key(vk, false, layout));
            inputs.Add(Key(vk, true, layout));
            if (shift)
                inputs.Add(Key(VK_SHIFT, true, layout));
            if (altGr)
            {
                inputs.Add(Key(VK_RMENU, true, layout, extended: true));
                inputs.Add(Key(VK_LCONTROL, true, layout));
            }

            EnsureTarget();
            SendInputs(inputs.ToArray());
            Pace();
        }
    }

    public static void Press(ushort vk, bool shift = false, bool ctrl = false)
    {
        var layout = Layout();
        var extended = vk is VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN or VK_HOME or VK_END;
        var inputs = new List<INPUT>();
        if (ctrl)
            inputs.Add(Key(VK_CONTROL, false, layout));
        if (shift)
            inputs.Add(Key(VK_SHIFT, false, layout));
        inputs.Add(Key(vk, false, layout, extended));
        inputs.Add(Key(vk, true, layout, extended));
        if (shift)
            inputs.Add(Key(VK_SHIFT, true, layout));
        if (ctrl)
            inputs.Add(Key(VK_CONTROL, true, layout));
        EnsureTarget();
        SendInputs(inputs.ToArray());
        Pace();
    }

    public static void SelectAllAndDelete()
    {
        Press(VK_A, ctrl: true);
        Press(VK_BACK);
    }

    /// <summary>
    /// Typing speed: 60 ms per key (~200 words per minute), faster than nearly anyone types. Windows 11 Notepad
    /// applies queued keys against the live keyboard state when it falls behind, so at 15–30 ms per key it
    /// mistyped keys that followed an insertion (measured, about 1 run in 3); override with COMET_E2E_PACE_MS.
    /// </summary>
    private static void Pace() => Thread.Sleep(PaceMs);

    private static readonly int PaceMs = int.TryParse(Environment.GetEnvironmentVariable("COMET_E2E_PACE_MS"), out var ms) ? ms : 60;

    private static INPUT Key(ushort vk, bool up, nint layout, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKeyExW(vk, 0, layout),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };
}
