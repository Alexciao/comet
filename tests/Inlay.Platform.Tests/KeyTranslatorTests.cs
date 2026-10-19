using System.Runtime.InteropServices;
using Inlay.Core.Input;
using Inlay.Core.Shorthands;
using Inlay.Platform.Hook;

namespace Inlay.Platform.Tests;

/// <summary>
/// Key translation against real Windows keyboard layouts: Italian (where ':' is Shift+period and '@' is AltGr+ò)
/// US, and US-International (dead keys). A layout the machine doesn't have loaded is loaded for the test and unloaded
/// afterwards.
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

    /// <summary>H9 (AUDIT.md): holding Caps Lock auto-repeats its key-down; Windows toggles once, so must Inlay.</summary>
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

    private const string UsInternational = "00020409";
    private const uint VkOem7 = 0xDE, VkOemComma = 0xBC, VkSpace = 0x20, VkTab = 0x09;

    // US-International dead keys: ' " ` ~ ^.
    private static readonly (uint Vk, Modifiers Modifiers)[] s_usIntlDeadKeys =
        [(VkOem7, Modifiers.None), (VkOem7, Modifiers.Shift), (VkOem3, Modifiers.None), (VkOem3, Modifiers.Shift), ('6', Modifiers.Shift)];

    /// <summary>
    /// H13: after a dead key, what Inlay reads is what Windows types: another dead key types both accents, and
    /// Enter, Tab, Backspace and Escape come after the accent. Windows is asked directly (ToUnicodeEx changing this
    /// thread's keyboard state). Letters whose composition NFC gets differently from the layout's table (<c>~e</c>,
    /// <c>'c</c> → <c>ç</c>) are left out.
    /// </summary>
    [Fact]
    public void Us_international_dead_keys_read_as_windows_types_them()
    {
        var intl = Layout(UsInternational);
        (uint Vk, Modifiers Modifiers)[] next =
        [
            .. s_usIntlDeadKeys, (VkSpace, Modifiers.None), ('A', Modifiers.None), ('A', Modifiers.Shift), ('Q', Modifiers.None),
            (VkOemComma, Modifiers.None), (VkOemPeriod, Modifiers.None), ('1', Modifiers.Shift), (VkReturn, Modifiers.None),
            (VkTab, Modifiers.None), (VkBack, Modifiers.None), (VkEscape, Modifiers.None),
        ];
        foreach (var dead in s_usIntlDeadKeys)
        {
            foreach (var key in next)
            {
                WindowsTypes(intl, VkSpace, Modifiers.None); // nothing pending
                var expected = WindowsTypes(intl, dead.Vk, dead.Modifiers) + WindowsTypes(intl, key.Vk, key.Modifiers);
                var actual = Typed(Press(intl, dead.Vk, dead.Modifiers)) + Typed(Press(intl, key.Vk, key.Modifiers));
                Assert.Equal(expected, actual);
            }
        }
    }

    /// <summary>The text a translated key stands for, with Enter, Tab, Backspace and Escape as their control characters.</summary>
    private static string Typed(KeyInput[] keys) => string.Concat(keys.Select(k => k.Kind switch
    {
        KeyKind.Char => k.Char.ToString(),
        KeyKind.Enter => "\r",
        KeyKind.Tab => "\t",
        KeyKind.Backspace => "\b",
        KeyKind.Escape => "\u001B",
        KeyKind.DeadKey => "",
        _ => throw new InvalidOperationException(k.ToString()),
    }));

    /// <summary>What Windows types for a key, keeping a dead key pending on this thread as an app's thread would.</summary>
    private static unsafe string WindowsTypes(nint layout, uint vk, Modifiers modifiers)
    {
        var state = stackalloc byte[256];
        if ((modifiers & Modifiers.Shift) != 0)
            state[VkShift] = state[0xA0] = 0x80;
        var buffer = stackalloc char[8];
        var n = ToUnicodeEx(vk, MapVirtualKeyExW(vk, MapVkToVsc, layout), state, buffer, 8, 0, layout);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// <summary>H13, end to end on US-International: the key after a dead key never expands, so nothing is swallowed.</summary>
    [Theory]
    [InlineData(VkOem7, true, VkSpace, false)]      // brb + " + space = brb"
    [InlineData(VkOem7, true, 'Q', false)]          // brb + " + q = brb"q (the q was lost)
    [InlineData(VkOem7, true, VkOemPeriod, false)]
    [InlineData(VkOem7, true, VkOem7, true)]        // brb + " + " = brb""
    [InlineData(VkOem7, false, VkReturn, false)]    // brb + ' + Enter = brb' and a new line
    [InlineData(VkOem7, false, VkTab, false)]
    [InlineData(VkOem3, true, VkSpace, false)]
    public void Us_international_the_key_after_a_dead_key_does_not_expand(uint deadVk, bool deadShift, uint vk, bool shift)
    {
        var intl = Layout(UsInternational);
        var shorthands = Shorthands();
        TypeKeys(shorthands, intl, ('B', Modifiers.None), ('R', Modifiers.None), ('B', Modifiers.None));
        var decisions = TypeKeys(shorthands, intl,
            (deadVk, deadShift ? Modifiers.Shift : Modifiers.None), (vk, shift ? Modifiers.Shift : Modifiers.None));
        Assert.All(decisions, d => Assert.False(d.Swallow));
    }

    [Fact]
    public void Us_international_a_composed_letter_is_part_of_the_word()
    {
        var intl = Layout(UsInternational);
        var shorthands = Shorthands();
        TypeKeys(shorthands, intl, ('C', Modifiers.None), ('A', Modifiers.None), ('F', Modifiers.None), (VkOem7, Modifiers.None), ('E', Modifiers.None));
        var d = TypeKeys(shorthands, intl, (VkSpace, Modifiers.None)).Single();
        Assert.Equal(ShorthandAction.Expand, d.Action);
        Assert.Equal("café", d.Deleted);
    }

    [Fact]
    public void Us_international_after_a_dead_key_is_resolved_words_expand_again()
    {
        var intl = Layout(UsInternational);
        var shorthands = Shorthands();
        TypeKeys(shorthands, intl, (VkOem7, Modifiers.Shift), ('Q', Modifiers.None), (VkSpace, Modifiers.None),
            ('B', Modifiers.None), ('R', Modifiers.None), ('B', Modifiers.None));
        Assert.Equal(ShorthandAction.Expand, TypeKeys(shorthands, intl, (VkSpace, Modifiers.None)).Single().Action);
    }

    /// <summary>Italian has no dead keys: the apostrophe is a character and words expand after it as before.</summary>
    [Fact]
    public void Italian_apostrophe_and_quote_are_characters_and_boundaries_expand()
    {
        var it = Layout("00000410");
        Assert.Equal('\'', Char(it, 0xDB));
        Assert.Equal('"', Char(it, '2', Modifiers.Shift));
        var shorthands = Shorthands();
        TypeKeys(shorthands, it, ('B', Modifiers.None), ('R', Modifiers.None), ('B', Modifiers.None));
        Assert.Equal(new Boundary(BoundaryKind.Char, '"'), TypeKeys(shorthands, it, ('2', Modifiers.Shift)).Single().Boundary);
    }

    private static ShorthandEngine Shorthands()
    {
        var e = new ShorthandEngine();
        e.SetRules([new ShorthandRule("brb", "be right back"), new ShorthandRule("café", "coffee shop")]);
        return e;
    }

    /// <summary>Presses keys, feeding what they translate to into <paramref name="shorthands"/> as the engine does.</summary>
    private List<ShorthandDecision> TypeKeys(ShorthandEngine shorthands, nint layout, params (uint Vk, Modifiers Modifiers)[] keys)
    {
        var decisions = new List<ShorthandDecision>();
        foreach (var (vk, modifiers) in keys)
        {
            foreach (var input in Press(layout, vk, modifiers))
                decisions.Add(shorthands.OnKey(input));
        }

        return decisions;
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

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buffer, int count, uint flags, nint hkl);
}
