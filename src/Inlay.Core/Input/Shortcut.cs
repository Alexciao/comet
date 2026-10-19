using System.Collections.Frozen;

namespace Inlay.Core.Input;

/// <summary>The modifiers of a <see cref="Shortcut"/>, in the order they are written: Win, Ctrl, Alt, Shift.</summary>
[Flags]
public enum ShortcutModifiers : byte
{
    None = 0,
    Win = 1,
    Ctrl = 2,
    Alt = 4,
    Shift = 8,
}

/// <summary>
/// A global keyboard shortcut such as <c>Win+Alt+I</c>: modifiers plus one key, the key being a Windows virtual-key
/// code. Punctuation keys are named by their virtual key (<c>Period</c>, <c>Semicolon</c>), not by the character they
/// type, so the setting means the same physical key on every keyboard layout; Settings shows the layout's character.
/// <para><see cref="None"/> (the empty string in settings) turns the shortcut off.</para>
/// </summary>
public readonly record struct Shortcut(ShortcutModifiers Modifiers, int Key)
{
    public static readonly Shortcut None;

    /// <summary>True for the "off" shortcut.</summary>
    public bool IsNone => Key == 0 && Modifiers == ShortcutModifiers.None;

    /// <summary>Why this shortcut can't be used, as a sentence for the user; null when it can (or when it is off).</summary>
    public string? Problem => IsNone ? null : ShortcutRules.Problem(this);

    /// <summary>A shortcut to register: not off, and allowed by the rules.</summary>
    public bool IsUsable => !IsNone && Problem is null;

    /// <summary>
    /// Reads a shortcut written as <c>Win+Alt+I</c> (case and spaces don't matter; <c>Control</c> and <c>Windows</c>
    /// are accepted too). An empty or blank string is <see cref="None"/>. Only the syntax is checked here; see
    /// <see cref="Problem"/> for whether the combination is allowed.
    /// </summary>
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = None;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var span = text.AsSpan().Trim();
        var modifiers = ShortcutModifiers.None;
        var key = 0;
        while (!span.IsEmpty)
        {
            // "Ctrl++" names the plus key: a '+' right after a separator (or at the start) is the key itself.
            int plus = span[0] == '+' ? (span.Length == 1 ? -1 : 1) : span.IndexOf('+');
            var token = (plus < 0 ? span : span[..plus]).Trim();
            span = plus < 0 ? [] : span[(plus + 1)..];
            if (plus >= 0 && span.Trim().IsEmpty)
                return false; // "Win+Alt+" has no key

            if (Modifier(token) is { } m)
            {
                if (key != 0 || (modifiers & m) != 0)
                    return false; // a modifier after the key, or twice
                modifiers |= m;
                continue;
            }

            if (key != 0 || !s_keysByName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(token, out key))
                return false;
        }

        if (key == 0)
            return false; // modifiers only
        shortcut = new Shortcut(modifiers, key);
        return true;
    }

    /// <summary>The shortcut a setting asks for, or <see cref="None"/> when it is off, unreadable or not allowed.</summary>
    public static Shortcut FromSetting(string? text) =>
        TryParse(text, out var s) && s.Problem is null ? s : None;

    /// <summary>The canonical text (<c>Win+Ctrl+Alt+Shift+Key</c> order); empty for <see cref="None"/>.</summary>
    public override string ToString() => string.Join('+', Parts(KeyName));

    /// <summary>
    /// The names to show one per key cap: the modifiers in canonical order, then the key (if any) as
    /// <paramref name="keyLabel"/> names it (default: <see cref="KeyName"/>).
    /// </summary>
    public IReadOnlyList<string> Parts(Func<int, string>? keyLabel = null)
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Win))
            parts.Add("Win");
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift))
            parts.Add("Shift");
        if (Key != 0)
            parts.Add((keyLabel ?? KeyName)(Key));
        return parts;
    }

    /// <summary>True for a virtual key that can be the key of a shortcut (see <see cref="KeyName"/>).</summary>
    public static bool IsSupportedKey(int vk) => s_namesByKey.ContainsKey(vk);

    /// <summary>
    /// The stable, layout-independent name of a supported key (<c>A</c>, <c>7</c>, <c>F13</c>, <c>Space</c>,
    /// <c>Period</c>); an unsupported key is written as its code (<c>VK_0x07</c>) so nothing is ever blank.
    /// </summary>
    public static string KeyName(int vk) => s_namesByKey.TryGetValue(vk, out var name) ? name : $"VK_0x{vk:X2}";

    /// <summary>True for keys that type a character on their own or with Shift (letters, digits, punctuation, space).</summary>
    public static bool IsPrintableKey(int vk) =>
        vk is VkSpace or (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= VkNumPad0 and <= VkDivide and not VkSeparator)
        or (>= VkOem1 and <= VkOem3) or (>= VkOem4 and <= VkOem8) or VkOem102;

    /// <summary>F13–F24: no keyboard has them as plain keys, so they may be a shortcut on their own.</summary>
    public static bool IsExtraFunctionKey(int vk) => vk is >= VkF13 and <= VkF24;

    private static ShortcutModifiers? Modifier(ReadOnlySpan<char> token) =>
        token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Win
        : token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Ctrl
        : token.Equals("Alt", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Alt
        : token.Equals("Shift", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Shift
        : null;

    // Virtual-key codes (WinUser.h) used by the rules and the name table.
    internal const int VkPause = 0x13;
    internal const int VkSpace = 0x20;
    internal const int VkPrior = 0x21;
    internal const int VkNext = 0x22;
    internal const int VkEnd = 0x23;
    internal const int VkHome = 0x24;
    internal const int VkLeft = 0x25;
    internal const int VkUp = 0x26;
    internal const int VkRight = 0x27;
    internal const int VkDown = 0x28;
    internal const int VkInsert = 0x2D;
    internal const int VkDelete = 0x2E;
    internal const int VkNumPad0 = 0x60;
    internal const int VkMultiply = 0x6A;
    internal const int VkAdd = 0x6B;
    internal const int VkSeparator = 0x6C;
    internal const int VkSubtract = 0x6D;
    internal const int VkDecimal = 0x6E;
    internal const int VkDivide = 0x6F;
    internal const int VkF1 = 0x70;
    internal const int VkF4 = 0x73;
    internal const int VkF13 = 0x7C;
    internal const int VkF23 = 0x86;
    internal const int VkF24 = 0x87;
    internal const int VkOem1 = 0xBA;      // ;: on US keyboards
    internal const int VkOemPlus = 0xBB;
    internal const int VkOemComma = 0xBC;
    internal const int VkOemMinus = 0xBD;
    internal const int VkOemPeriod = 0xBE;
    internal const int VkOem2 = 0xBF;      // /?
    internal const int VkOem3 = 0xC0;      // `~
    internal const int VkOem4 = 0xDB;      // [{
    internal const int VkOem5 = 0xDC;      // \|
    internal const int VkOem6 = 0xDD;      // ]}
    internal const int VkOem7 = 0xDE;      // '"
    internal const int VkOem8 = 0xDF;
    internal const int VkOem102 = 0xE2;    // <> next to left Shift on ISO keyboards

    private static readonly FrozenDictionary<int, string> s_namesByKey = BuildNames();

    private static readonly FrozenDictionary<string, int> s_keysByName = BuildKeys();

    private static FrozenDictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [VkSpace] = "Space",
            [VkPause] = "Pause",
            [VkPrior] = "PageUp",
            [VkNext] = "PageDown",
            [VkEnd] = "End",
            [VkHome] = "Home",
            [VkLeft] = "Left",
            [VkUp] = "Up",
            [VkRight] = "Right",
            [VkDown] = "Down",
            [VkInsert] = "Insert",
            [VkDelete] = "Delete",
            [VkMultiply] = "NumPadMultiply",
            [VkAdd] = "NumPadPlus",
            [VkSubtract] = "NumPadMinus",
            [VkDecimal] = "NumPadDecimal",
            [VkDivide] = "NumPadDivide",
            [VkOem1] = "Semicolon",
            [VkOemPlus] = "Plus",
            [VkOemComma] = "Comma",
            [VkOemMinus] = "Minus",
            [VkOemPeriod] = "Period",
            [VkOem2] = "Slash",
            [VkOem3] = "Backtick",
            [VkOem4] = "OpenBracket",
            [VkOem5] = "Backslash",
            [VkOem6] = "CloseBracket",
            [VkOem7] = "Quote",
            [VkOem8] = "Oem8",
            [VkOem102] = "Oem102",
        };
        for (var c = 'A'; c <= 'Z'; c++)
            names[c] = c.ToString();
        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = c.ToString();
            names[VkNumPad0 + (c - '0')] = $"NumPad{c}";
        }

        for (var f = 1; f <= 24; f++)
            names[VkF1 + f - 1] = $"F{f}";
        return names.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, int> BuildKeys()
    {
        var keys = s_namesByKey.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
        // What people write by hand (US characters for the punctuation keys, common spellings).
        keys[";"] = VkOem1;
        keys["="] = VkOemPlus;
        keys["+"] = VkOemPlus;
        keys[","] = VkOemComma;
        keys["-"] = VkOemMinus;
        keys["."] = VkOemPeriod;
        keys["/"] = VkOem2;
        keys["`"] = VkOem3;
        keys["["] = VkOem4;
        keys["\\"] = VkOem5;
        keys["]"] = VkOem6;
        keys["'"] = VkOem7;
        keys["PgUp"] = VkPrior;
        keys["PgDn"] = VkNext;
        keys["Ins"] = VkInsert;
        keys["Del"] = VkDelete;
        return keys.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Which shortcuts Inlay accepts, and the sentence that explains a refusal.</summary>
internal static class ShortcutRules
{
    private const ShortcutModifiers Win = ShortcutModifiers.Win;
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Ctrl;
    private const ShortcutModifiers Alt = ShortcutModifiers.Alt;
    private const ShortcutModifiers Shift = ShortcutModifiers.Shift;

    public static string? Problem(Shortcut s)
    {
        var m = s.Modifiers;
        if (s.Key == 0)
            return "Add a key, such as a letter, to the combination.";
        if (!Shortcut.IsSupportedKey(s.Key))
            return "Inlay can't use this key in a shortcut. Choose another key.";
        // Italian, German, French, Polish… type @ # [ ] € with AltGr, which Windows reports as Ctrl+Alt.
        if (m.HasFlag(Ctrl) && m.HasFlag(Alt))
            return "Ctrl+Alt is AltGr on many keyboards. Choose another combination.";
        var extra = Shortcut.IsExtraFunctionKey(s.Key); // F13–F24 may stand alone
        if (m == ShortcutModifiers.None)
            return extra ? null : "A shortcut needs Win, Ctrl or Alt. Add one of them to the key.";
        if (m == Shift && !extra)
        {
            return Shortcut.IsPrintableKey(s.Key)
                ? "Shift with this key types a character. Add Win, Ctrl or Alt."
                : "Apps use Shift with this key to select or edit. Add Win, Ctrl or Alt.";
        }

        if (Owner(s) is { } owner)
            return $"{owner} {s}. Choose another combination.";
        return null;
    }

    /// <summary>Who already uses this combination, as "X uses" (best effort: Windows 11, PowerToys defaults, universal app commands).</summary>
    private static string? Owner(Shortcut s)
    {
        var m = s.Modifiers;
        var k = s.Key;
        // Windows assigns nearly every Win+key: letters (Win+L locks), digits (taskbar), punctuation (Win+. emoji,
        // Win+, peek, Win+Plus magnifier), Space (input language), arrows (snap), Tab, Home, Pause, Print Screen…
        if (m == Win && k is not (>= Shortcut.VkF1 and <= Shortcut.VkF24))
            return "Windows uses";
        if (s_windows.Contains(s))
            return "Windows uses";
        if ((m is (Win | Shift) or (Win | Ctrl) or (Win | Alt)) && k is >= '0' and <= '9')
            return "Windows uses"; // taskbar apps: new window, switch window, jump list
        if (s_powerToys.Contains(s))
            return "PowerToys uses";
        if (s_apps.Contains(s))
            return "Most apps use";
        return null;
    }

    private static Shortcut S(ShortcutModifiers m, int key) => new(m, key);

    private static readonly FrozenSet<Shortcut> s_windows = new[]
    {
        S(Alt, Shortcut.VkF4), S(Alt, Shortcut.VkSpace),
        S(Win | Shift, 'S'), S(Win | Shift, 'M'), S(Win | Shift, 'R'), S(Win | Shift, Shortcut.VkSpace),
        S(Win | Shift, Shortcut.VkLeft), S(Win | Shift, Shortcut.VkRight), S(Win | Shift, Shortcut.VkUp), S(Win | Shift, Shortcut.VkDown),
        S(Win | Ctrl, 'C'), S(Win | Ctrl, 'D'), S(Win | Ctrl, 'F'), S(Win | Ctrl, 'N'), S(Win | Ctrl, 'O'), S(Win | Ctrl, 'Q'),
        S(Win | Ctrl, 'S'), S(Win | Ctrl, 'V'), S(Win | Ctrl, Shortcut.VkF4),
        S(Win | Ctrl, Shortcut.VkLeft), S(Win | Ctrl, Shortcut.VkRight), S(Win | Ctrl, Shortcut.VkSpace),
        S(Win | Ctrl | Shift, 'B'),
        S(Win | Alt, 'B'), S(Win | Alt, 'D'), S(Win | Alt, 'G'), S(Win | Alt, 'H'), S(Win | Alt, 'K'), S(Win | Alt, 'M'),
        S(Win | Alt, 'R'), S(Win | Alt, 'T'), S(Win | Alt, Shortcut.VkUp), S(Win | Alt, Shortcut.VkDown),
        S(Win | Shift, Shortcut.VkF23), // the Copilot key
    }.ToFrozenSet();

    private static readonly FrozenSet<Shortcut> s_powerToys = new[]
    {
        S(Win | Alt, Shortcut.VkSpace), S(Win | Alt, 'P'), S(Win | Shift, 'C'), S(Win | Shift, 'T'), S(Win | Shift, 'V'),
        S(Win | Shift, 'D'), S(Win | Shift, 'H'), S(Win | Shift, Shortcut.VkOem2), S(Win | Shift, Shortcut.VkOem3),
        S(Win | Ctrl, 'T'), S(Win | Ctrl, Shortcut.VkOem3), S(Win | Ctrl | Shift, 'T'), S(Win | Ctrl | Shift, 'M'), S(Win | Ctrl | Shift, 'R'),
    }.ToFrozenSet();

    private static readonly FrozenSet<Shortcut> s_apps = new[]
    {
        S(Ctrl, 'A'), S(Ctrl, 'C'), S(Ctrl, 'F'), S(Ctrl, 'N'), S(Ctrl, 'O'), S(Ctrl, 'P'), S(Ctrl, 'S'), S(Ctrl, 'V'),
        S(Ctrl, 'W'), S(Ctrl, 'X'), S(Ctrl, 'Y'), S(Ctrl, 'Z'), S(Ctrl | Shift, 'Z'),
    }.ToFrozenSet();
}
