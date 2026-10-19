using Inlay.Core.Input;
using Inlay.Platform.Native;

namespace Inlay.Platform.Input;

/// <summary>
/// How a shortcut's keys are written on screen: punctuation keys as the character they type with the current
/// keyboard layout (on an Italian keyboard <c>Semicolon</c> shows as <c>è</c>), other keys by a readable name.
/// Settings stores <see cref="Shortcut.ToString"/>; only the display follows the layout.
/// </summary>
internal static class ShortcutLabels
{
    private const uint MAPVK_VK_TO_CHAR = 2;
    private const uint DeadKeyFlag = 0x80000000;

    /// <summary>The key caps to show for <paramref name="shortcut"/>, modifiers first.</summary>
    public static IReadOnlyList<string> Parts(Shortcut shortcut) => shortcut.Parts(KeyLabel);

    /// <summary><see cref="Parts"/> joined with <c>+</c> (for tooltips and screen readers).</summary>
    public static string Text(Shortcut shortcut) => string.Join(" + ", Parts(shortcut));

    /// <summary>The label of one key, using the calling thread's keyboard layout for punctuation.</summary>
    public static string KeyLabel(int vk)
    {
        if (IsPunctuation(vk) && LayoutChar(vk) is { } c)
            return c.ToString();
        return vk switch
        {
            0x21 => "Page Up",
            0x22 => "Page Down",
            >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            _ => Shortcut.KeyName(vk),
        };
    }

    private static bool IsPunctuation(int vk) => vk is (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2;

    private static char? LayoutChar(int vk)
    {
        var mapped = User32.MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_CHAR, User32.GetKeyboardLayout(0)) & ~DeadKeyFlag;
        if (mapped is 0 or > char.MaxValue)
            return null;
        var c = (char)mapped;
        return char.IsControl(c) || char.IsWhiteSpace(c) || char.IsSurrogate(c) ? null : c;
    }
}
