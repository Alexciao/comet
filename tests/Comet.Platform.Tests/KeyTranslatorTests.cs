using System.Runtime.InteropServices;
using Comet.Core.Input;
using Comet.Platform.Hook;

namespace Comet.Platform.Tests;

/// <summary>
/// Key translation against real Windows keyboard layouts: Italian (where ':' is Shift+period and '@' is AltGr+ò)
/// and US. A layout the machine doesn't have loaded is loaded for the test and unloaded afterwards.
/// </summary>
public sealed partial class KeyTranslatorTests : IDisposable
{
    private const uint KlfNoTellShell = 0x80;
    private const uint MapVkToVsc = 0;

    private const uint VkShift = 0x10, VkCapital = 0x14, VkReturn = 0x0D, VkBack = 0x08, VkEscape = 0x1B, VkLeft = 0x25;
    private const uint VkOem1 = 0xBA, VkOemPeriod = 0xBE, VkOem3 = 0xC0, VkPacket = 0xE7, VkProcessKey = 0xE5;

    private readonly List<nint> _loadedHere = [];
    private readonly KeyTranslator _translator = new();

    private nint Layout(string klid)
    {
        var before = LoadedLayouts();
        var hkl = LoadKeyboardLayoutW(klid, KlfNoTellShell);
        Assert.NotEqual(0, hkl);
        if (!before.Contains(hkl))
            _loadedHere.Add(hkl);
        return hkl;
    }

    public void Dispose()
    {
        foreach (var hkl in _loadedHere)
            UnloadKeyboardLayout(hkl);
    }

    /// <summary>A key-down, then (unless <paramref name="hold"/>) its key-up.</summary>
    private KeyInput[] Press(nint layout, uint vk, Modifiers modifiers = Modifiers.None, bool hold = false)
    {
        Span<KeyInput> output = stackalloc KeyInput[2];
        var n = _translator.Translate(vk, MapVirtualKeyExW(vk, MapVkToVsc, layout), modifiers, layout, output);
        if (!hold)
            _translator.KeyUp(vk);
        return output[..n].ToArray();
    }

    /// <summary>H9 (AUDIT.md): holding Caps Lock auto-repeats its key-down; Windows toggles once, so must Comet.</summary>
    [Fact]
    public void Holding_caps_lock_toggles_once()
    {
        var us = Layout("00000409");
        var initial = Char(us, 'A');
        Press(us, VkCapital, hold: true);
        Press(us, VkCapital, hold: true);
        Press(us, VkCapital, hold: true);
        _translator.KeyUp(VkCapital);
        Assert.NotEqual(initial, Char(us, 'A'));
        Press(us, VkCapital);
        Assert.Equal(initial, Char(us, 'A'));
    }

    /// <summary>A5 (AUDIT.md): Insert (Shift+Insert pastes, Ctrl+Insert copies) resets the word like the caret keys.</summary>
    [Fact]
    public void Insert_is_navigation()
    {
        var us = Layout("00000409");
        Assert.Equal(KeyKind.Navigation, Press(us, 0x2D, Modifiers.Shift).Single().Kind);
        Assert.Equal(KeyKind.Navigation, Press(us, 0x2D).Single().Kind);
    }

    private char Char(nint layout, uint vk, Modifiers modifiers = Modifiers.None)
    {
        var keys = Press(layout, vk, modifiers);
        Assert.Single(keys);
        Assert.Equal(KeyKind.Char, keys[0].Kind);
        return keys[0].Char;
    }

    private static readonly Modifiers s_altGr = Modifiers.Control | Modifiers.Alt;

    [Fact]
    public void Italian_colon_is_shift_period_and_at_sign_is_altgr_o_grave()
    {
        var it = Layout("00000410");
        Assert.Equal(':', Char(it, VkOemPeriod, Modifiers.Shift));
        Assert.Equal('.', Char(it, VkOemPeriod));
        Assert.Equal('@', Char(it, VkOem3, s_altGr));
        Assert.Equal('ò', Char(it, VkOem3));
        Assert.Equal('è', Char(it, VkOem1));
        Assert.Equal('/', Char(it, '7', Modifiers.Shift));
        Assert.Equal('€', Char(it, 'E', s_altGr));
    }

    [Fact]
    public void Us_colon_is_shift_semicolon_and_at_sign_is_shift_2()
    {
        var us = Layout("00000409");
        Assert.Equal(':', Char(us, VkOem1, Modifiers.Shift));
        Assert.Equal(';', Char(us, VkOem1));
        Assert.Equal('@', Char(us, '2', Modifiers.Shift));
        Assert.Equal('.', Char(us, VkOemPeriod));
    }

    [Fact]
    public void Letters_follow_shift_and_caps_lock()
    {
        var us = Layout("00000409");
        var initial = Char(us, 'A');
        Press(us, VkCapital);
        var toggled = Char(us, 'A');
        Press(us, VkCapital);
        Assert.Equal(char.ToUpperInvariant(initial) == initial ? 'a' : 'A', toggled);
        Assert.Equal(initial, Char(us, 'A'));
        Assert.Equal(initial == 'a' ? 'A' : 'a', Char(us, 'A', Modifiers.Shift));
    }

    [Fact]
    public void Ctrl_alt_and_win_combinations_are_shortcuts_not_text()
    {
        var it = Layout("00000410");
        Assert.Equal(KeyKind.Shortcut, Press(it, 'C', Modifiers.Control).Single().Kind);
        Assert.Equal(KeyKind.Shortcut, Press(it, VkOemPeriod, Modifiers.Alt).Single().Kind);
        Assert.Equal(KeyKind.Shortcut, Press(it, 'V', Modifiers.Windows).Single().Kind);
        Assert.Equal(KeyKind.Shortcut, Press(it, VkBack, Modifiers.Control).Single().Kind);
    }

    [Fact]
    public void Editing_and_navigation_keys_have_their_own_kinds()
    {
        var it = Layout("00000410");
        Assert.Equal(new KeyInput(KeyKind.Enter), Press(it, VkReturn).Single());
        Assert.Equal(new KeyInput(KeyKind.Enter, Shift: true), Press(it, VkReturn, Modifiers.Shift).Single());
        Assert.Equal(KeyKind.Backspace, Press(it, VkBack).Single().Kind);
        Assert.Equal(KeyKind.Escape, Press(it, VkEscape).Single().Kind);
        Assert.Equal(KeyKind.Navigation, Press(it, VkLeft).Single().Kind);
        Assert.Equal(KeyKind.Ignored, Press(it, VkShift).Single().Kind);
    }

    [Fact]
    public void Unicode_packets_carry_their_character_and_ime_keys_are_reported()
    {
        var it = Layout("00000410");
        Span<KeyInput> output = stackalloc KeyInput[2];
        Assert.Equal(1, _translator.Translate(VkPacket, 'é', Modifiers.None, it, output));
        Assert.Equal(KeyInput.Text('é'), output[0]);
        Assert.Equal(KeyKind.Ime, Press(it, VkProcessKey).Single().Kind);
    }

    [Theory]
    [InlineData('^', 'e', "ê")]
    [InlineData('`', 'a', "à")]
    [InlineData('´', 'E', "É")]
    [InlineData('¨', 'u', "ü")]
    [InlineData('~', 'n', "ñ")]
    [InlineData('´', ' ', "´")]
    [InlineData('~', 'q', "~q")]
    public void Dead_keys_compose_like_windows(char dead, char baseChar, string expected)
    {
        Span<KeyInput> output = stackalloc KeyInput[2];
        var n = KeyTranslator.Compose(dead, baseChar, output);
        Assert.Equal(expected, new string(output[..n].ToArray().Select(k => k.Char).ToArray()));
    }

    private static HashSet<nint> LoadedLayouts()
    {
        var buffer = new nint[64];
        var n = GetKeyboardLayoutList(buffer.Length, buffer);
        return [.. buffer.Take(n)];
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadKeyboardLayoutW(string klid, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnloadKeyboardLayout(nint hkl);

    [LibraryImport("user32.dll")]
    private static partial int GetKeyboardLayoutList(int count, [Out] nint[] list);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint hkl);
}
