using Inlay.Core.Input;
using Inlay.Core.Settings;
using Inlay.Platform.Input;
using Inlay.Platform.Native;

namespace Inlay.Platform.Tests;

/// <summary>
/// <see cref="GlobalShortcut"/> against the real <c>RegisterHotKey</c>, on a message-only window. Another app may
/// hold a combination on a developer's machine or a CI runner; those tests skip rather than fail.
/// </summary>
public sealed class HotKeyTests : IDisposable
{
    private readonly nint _hwnd = User32.CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, 0, 0);

    public HotKeyTests() => Assert.NotEqual(0, _hwnd);

    public void Dispose() => User32.DestroyWindow(_hwnd);

    private static Shortcut Parse(string text)
    {
        Assert.True(Shortcut.TryParse(text, out var s));
        return s;
    }

    private static void SkipIfInUse(ShortcutRegistration result, Shortcut shortcut)
    {
        if (result == ShortcutRegistration.InUse)
            Assert.Skip($"{shortcut} is registered by another app on this machine.");
    }

    [Fact]
    public void The_default_shortcut_registers_and_unregisters()
    {
        var shortcut = Parse(AppSettings.DefaultShortcut);
        using (var hotKey = new GlobalShortcut(_hwnd, 1))
        {
            var result = hotKey.Set(shortcut);
            SkipIfInUse(result, shortcut);
            Assert.Equal(ShortcutRegistration.Registered, result);
            Assert.True(hotKey.IsRegistered);
        }

        // Disposed: the combination is free again.
        var again = GlobalShortcut.TryRegister(_hwnd, 2, shortcut);
        Assert.Equal(ShortcutRegistration.Registered, again);
        Assert.True(UnregisterHotKey(2));
    }

    [Fact]
    public void A_combination_held_elsewhere_is_reported_in_use()
    {
        var shortcut = Parse("Win+Alt+F17");
        var first = GlobalShortcut.TryRegister(_hwnd, 10, shortcut);
        SkipIfInUse(first, shortcut);
        Assert.Equal(ShortcutRegistration.Registered, first);
        try
        {
            using var hotKey = new GlobalShortcut(_hwnd, 20);
            Assert.Equal(ShortcutRegistration.InUse, hotKey.Test(shortcut));
            Assert.Equal(ShortcutRegistration.InUse, hotKey.Set(shortcut));
            Assert.False(hotKey.IsRegistered);
            Assert.Equal(shortcut, hotKey.Shortcut); // kept, so setting it again retries

            Assert.True(UnregisterHotKey(10));
            Assert.Equal(ShortcutRegistration.Registered, hotKey.Set(shortcut));
        }
        finally
        {
            UnregisterHotKey(10);
        }
    }

    [Fact]
    public void Test_leaves_the_registration_alone()
    {
        var current = Parse("Win+Alt+F14");
        var other = Parse("Win+Alt+F15");
        using var hotKey = new GlobalShortcut(_hwnd, 30);
        var result = hotKey.Set(current);
        SkipIfInUse(result, current);

        Assert.Equal(ShortcutRegistration.Registered, hotKey.Test(current)); // ours, not "in use"
        var test = hotKey.Test(other);
        SkipIfInUse(test, other);
        Assert.Equal(ShortcutRegistration.Registered, test);
        Assert.Equal(current, hotKey.Shortcut);
        Assert.True(hotKey.IsRegistered);
        Assert.Equal(ShortcutRegistration.Registered, GlobalShortcut.TryRegister(_hwnd, 31, other)); // released after the test
        UnregisterHotKey(31);
    }

    [Fact]
    public void Suspending_releases_the_combination_and_resuming_takes_it_back()
    {
        var shortcut = Parse("Win+Alt+F16");
        using var hotKey = new GlobalShortcut(_hwnd, 40);
        SkipIfInUse(hotKey.Set(shortcut), shortcut);

        hotKey.IsSuspended = true;
        Assert.False(hotKey.IsRegistered);
        Assert.Equal(ShortcutRegistration.Registered, GlobalShortcut.TryRegister(_hwnd, 41, shortcut));
        UnregisterHotKey(41);

        hotKey.IsSuspended = false;
        Assert.True(hotKey.IsRegistered);
    }

    [Fact]
    public void Off_and_disallowed_shortcuts_register_nothing()
    {
        using var hotKey = new GlobalShortcut(_hwnd, 50);
        Assert.Equal(ShortcutRegistration.Off, hotKey.Set(Shortcut.None));
        Assert.Equal(ShortcutRegistration.NotAllowed, hotKey.Set(Parse("Ctrl+Alt+I")));
        Assert.Equal(ShortcutRegistration.NotAllowed, hotKey.Test(Parse("Win+L")));
        Assert.False(hotKey.IsRegistered);
    }

    [Fact]
    public void Recognizes_its_own_WM_HOTKEY()
    {
        using var hotKey = new GlobalShortcut(_hwnd, 60);
        Assert.True(hotKey.IsPressed(GlobalShortcut.WM_HOTKEY, 60));
        Assert.False(hotKey.IsPressed(GlobalShortcut.WM_HOTKEY, 61));
        Assert.False(hotKey.IsPressed(User32.WM_NULL, 60));
    }

    [Fact]
    public void Labels_name_keys_and_follow_the_layout_for_punctuation()
    {
        Assert.Equal(["Win", "Alt", "I"], ShortcutLabels.Parts(Parse("Win+Alt+I")));
        Assert.Equal("Num 7", ShortcutLabels.KeyLabel(0x67));
        Assert.Equal("Page Up", ShortcutLabels.KeyLabel(0x21));
        Assert.Equal("F13", ShortcutLabels.KeyLabel(0x7C));
        var period = ShortcutLabels.KeyLabel(0xBE); // "." on most layouts; never blank
        Assert.False(string.IsNullOrWhiteSpace(period));
        Assert.Equal("Win + Alt + I", ShortcutLabels.Text(Parse("Win+Alt+I")));
    }

    private bool UnregisterHotKey(int id) => GlobalShortcut.Release(_hwnd, id);
}
