using Inlay.Core.Input;
using Inlay.Core.Settings;

namespace Inlay.Core.Tests.Input;

public sealed class ShortcutTests
{
    private const ShortcutModifiers Win = ShortcutModifiers.Win;
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Ctrl;
    private const ShortcutModifiers Alt = ShortcutModifiers.Alt;
    private const ShortcutModifiers Shift = ShortcutModifiers.Shift;

    private static Shortcut Parse(string text)
    {
        Assert.True(Shortcut.TryParse(text, out var s), $"'{text}' should parse");
        return s;
    }

    [Theory]
    [InlineData("Win+Alt+I", Win | Alt, 'I')]
    [InlineData("Ctrl+Shift+Space", Ctrl | Shift, 0x20)]
    [InlineData("Win+Alt+Period", Win | Alt, 0xBE)]
    [InlineData("Win+Shift+F5", Win | Shift, 0x74)]
    [InlineData("F13", ShortcutModifiers.None, 0x7C)]
    [InlineData("Alt+Semicolon", Alt, 0xBA)]
    [InlineData("Ctrl+Shift+Oem102", Ctrl | Shift, 0xE2)]
    [InlineData("Win+Alt+NumPad7", Win | Alt, 0x67)]
    public void Parses_modifiers_and_key(string text, ShortcutModifiers modifiers, int key)
    {
        Assert.Equal(new Shortcut(modifiers, key), Parse(text));
    }

    [Theory]
    [InlineData("alt+win+i", "Win+Alt+I")]
    [InlineData(" Shift + Control + space ", "Ctrl+Shift+Space")]
    [InlineData("Windows+Alt+.", "Win+Alt+Period")]
    [InlineData("Ctrl+Shift+;", "Ctrl+Shift+Semicolon")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    [InlineData("Ctrl+Shift+=", "Ctrl+Shift+Plus")]
    [InlineData("Win+Alt+pgdn", "Win+Alt+PageDown")]
    [InlineData("Win+Ctrl+Alt+Shift+Z", "Win+Ctrl+Alt+Shift+Z")]
    public void Formats_in_canonical_order_with_stable_names(string text, string expected)
    {
        var s = Parse(text);
        Assert.Equal(expected, s.ToString());
        Assert.Equal(s, Parse(s.ToString())); // round trip
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_is_off(string? text)
    {
        Assert.True(Shortcut.TryParse(text, out var s));
        Assert.True(s.IsNone);
        Assert.Null(s.Problem);
        Assert.False(s.IsUsable);
        Assert.Equal("", s.ToString());
        Assert.Empty(s.Parts());
    }

    [Theory]
    [InlineData("Win+Alt")]              // no key
    [InlineData("Win+Alt+")]             // dangling separator
    [InlineData("Win+Alt+I+J")]          // two keys
    [InlineData("I+Alt")]                // modifier after the key
    [InlineData("Alt+Alt+I")]            // modifier twice
    [InlineData("Win+Alt+Banana")]       // unknown key
    [InlineData("Hyper+I")]              // unknown modifier
    [InlineData("Win+Alt+Tab")]          // not a key shortcuts can use
    public void Rejects_what_is_not_a_shortcut(string text)
    {
        Assert.False(Shortcut.TryParse(text, out var s));
        Assert.True(s.IsNone);
    }

    [Theory]
    [InlineData("Win+Alt+I")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Win+Alt+Period")]
    [InlineData("Win+Shift+I")]
    [InlineData("Alt+Shift+Q")]
    [InlineData("Win+F5")]
    [InlineData("F13")]
    [InlineData("Shift+F24")]
    [InlineData("Ctrl+F12")]
    public void Accepts_free_combinations(string text)
    {
        var s = Parse(text);
        Assert.Null(s.Problem);
        Assert.True(s.IsUsable);
    }

    [Theory]
    [InlineData("Ctrl+Alt+I")]
    [InlineData("Ctrl+Alt+Period")]
    [InlineData("Win+Ctrl+Alt+I")]
    [InlineData("Ctrl+Alt+Shift+F13")]
    public void Rejects_ctrl_alt_because_it_is_altgr(string text)
    {
        Assert.Equal("Ctrl+Alt is AltGr on many keyboards. Choose another combination.", Parse(text).Problem);
    }

    [Theory]
    [InlineData("I")]
    [InlineData("Space")]
    [InlineData("F5")]
    public void A_plain_key_needs_a_modifier_except_F13_to_F24(string text)
    {
        Assert.Equal("A shortcut needs Win, Ctrl or Alt. Add one of them to the key.", Parse(text).Problem);
    }

    [Theory]
    [InlineData("Shift+I")]
    [InlineData("Shift+7")]
    [InlineData("Shift+Period")]
    [InlineData("Shift+Space")]
    [InlineData("Shift+NumPad1")]
    public void Rejects_shift_with_a_key_that_types(string text)
    {
        Assert.Equal("Shift with this key types a character. Add Win, Ctrl or Alt.", Parse(text).Problem);
    }

    [Theory]
    [InlineData("Shift+Left")]
    [InlineData("Shift+Insert")]
    [InlineData("Shift+F10")]
    public void Rejects_shift_with_an_editing_key(string text)
    {
        Assert.Equal("Apps use Shift with this key to select or edit. Add Win, Ctrl or Alt.", Parse(text).Problem);
    }

    [Theory]
    [InlineData("Win+L", "Windows uses Win+L. Choose another combination.")]
    [InlineData("Win+Period", "Windows uses Win+Period. Choose another combination.")]
    [InlineData("Win+Semicolon", "Windows uses Win+Semicolon. Choose another combination.")]
    [InlineData("Win+Shift+S", "Windows uses Win+Shift+S. Choose another combination.")]
    [InlineData("Win+Ctrl+D", "Windows uses Win+Ctrl+D. Choose another combination.")]
    [InlineData("Win+Alt+K", "Windows uses Win+Alt+K. Choose another combination.")]
    [InlineData("Win+Shift+3", "Windows uses Win+Shift+3. Choose another combination.")]
    [InlineData("Win+Shift+F23", "Windows uses Win+Shift+F23. Choose another combination.")]
    [InlineData("Alt+F4", "Windows uses Alt+F4. Choose another combination.")]
    [InlineData("Alt+Space", "Windows uses Alt+Space. Choose another combination.")]
    [InlineData("Win+Alt+Space", "PowerToys uses Win+Alt+Space. Choose another combination.")]
    [InlineData("Win+Shift+T", "PowerToys uses Win+Shift+T. Choose another combination.")]
    [InlineData("Win+Shift+C", "PowerToys uses Win+Shift+C. Choose another combination.")]
    [InlineData("Ctrl+V", "Most apps use Ctrl+V. Choose another combination.")]
    public void Rejects_combinations_others_own(string text, string message)
    {
        Assert.Equal(message, Parse(text).Problem);
    }

    [Fact]
    public void A_shortcut_without_a_key_asks_for_one()
    {
        Assert.Equal("Add a key, such as a letter, to the combination.", new Shortcut(Win | Alt, 0).Problem);
    }

    [Fact]
    public void Messages_are_plain()
    {
        string[] samples = ["Ctrl+Alt+I", "I", "Shift+I", "Shift+Left", "Win+L", "Ctrl+V"];
        foreach (var text in samples)
        {
            var problem = Parse(text).Problem!;
            Assert.DoesNotContain("!", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("Oops", problem, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parts_list_modifiers_then_the_key_with_an_optional_label()
    {
        var s = Parse("Shift+Alt+Win+Period");
        Assert.Equal(["Win", "Alt", "Shift", "Period"], s.Parts());
        Assert.Equal(["Win", "Alt", "Shift", "."], s.Parts(_ => "."));
        Assert.Equal(["Win", "Alt"], new Shortcut(Win | Alt, 0).Parts());
    }

    [Fact]
    public void FromSetting_turns_unusable_text_off()
    {
        Assert.Equal(Parse("Win+Alt+I"), Shortcut.FromSetting("Win+Alt+I"));
        Assert.True(Shortcut.FromSetting("Ctrl+Alt+I").IsNone);
        Assert.True(Shortcut.FromSetting("nonsense").IsNone);
        Assert.True(Shortcut.FromSetting("").IsNone);
    }

    [Fact]
    public void The_default_setting_is_usable()
    {
        var s = Parse(AppSettings.DefaultShortcut);
        Assert.True(s.IsUsable, s.Problem);
        Assert.False(s.Modifiers.HasFlag(Ctrl) && s.Modifiers.HasFlag(Alt));
    }

    [Fact]
    public void Every_supported_key_has_a_name_that_parses_back()
    {
        for (var vk = 1; vk < 256; vk++)
        {
            if (!Shortcut.IsSupportedKey(vk))
                continue;
            var s = new Shortcut(Win | Alt, vk);
            Assert.Equal(s, Parse(s.ToString()));
        }
    }

    [Fact]
    public void Unsupported_keys_still_have_a_name()
    {
        Assert.False(Shortcut.IsSupportedKey(0x09));
        Assert.Equal("VK_0x09", Shortcut.KeyName(0x09));
        Assert.Equal("Inlay can't use this key in a shortcut. Choose another key.", new Shortcut(Win | Alt, 0x09).Problem);
    }
}
