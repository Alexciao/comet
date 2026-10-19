using System.Runtime.InteropServices;
using Inlay.E2E.Harness;
using FlaUI.Core.AutomationElements;

namespace Inlay.E2E;

/// <summary>
/// System events Inlay must survive: Explorer restarting, unlock, resume from sleep, display changes, and a second
/// monitor. The events are delivered as the real window messages Windows sends to Inlay's message window.
/// </summary>
[Collection(Desktop.Name)]
public sealed partial class ResilienceTests : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218, PbtApmResumeAutomatic = 0x12;
    private const uint WmWtsSessionChange = 0x02B1, WtsSessionUnlock = 0x8;
    private const uint WmDisplayChange = 0x007E;

    private readonly InlayApp _inlay = new InlayApp().WithShorthands(("cmq", "comunque"));
    private EdgePage? _edge;

    public void Dispose()
    {
        _edge?.Dispose();
        _inlay.Dispose();
    }

    private static nint MessageWindow() => Native.FindWindowW("Inlay.MessageWindow", null);

    private ITextTarget Field()
    {
        _edge ??= new EdgePage();
        return _edge.Textarea;
    }

    /// <summary>Picker and shorthands both still work, from a clean field.</summary>
    private void AssertStillWorks(ITextTarget field)
    {
        field.Focus();
        Keyboard.SelectAllAndDelete();
        Keyboard.Type("cmq :fire:");
        Assert.Equal("comunque 🔥", Wait.For(field.ReadText, t => t == "comunque 🔥", TimeSpan.FromSeconds(4), "Inlay to still work"));
    }

    [Fact]
    public void Explorer_restart_brings_the_tray_icon_back()
    {
        _inlay.Start();
        Native.PostMessageW(MessageWindow(), RegisterWindowMessageW("TaskbarCreated"), 0, 0);
        Wait.Until(() => _inlay.Log.Contains("taskbar recreated: tray icon re-added", StringComparison.Ordinal), TimeSpan.FromSeconds(5), "the tray icon to be re-added");
        AssertStillWorks(Field());
    }

    [Fact]
    public void Unlock_and_resume_reset_state_and_keep_the_hook()
    {
        _inlay.Start();
        var field = Field();
        field.Focus();
        Keyboard.Type(":smi");
        _inlay.WaitForPopup();

        // Unlocking (or resuming) mid-session forgets the session and checks the hook is still installed.
        Native.PostMessageW(MessageWindow(), WmWtsSessionChange, WtsSessionUnlock, 0);
        _inlay.WaitForPopupClosed();
        Wait.Until(() => _inlay.Log.Contains("session unlocked", StringComparison.Ordinal), TimeSpan.FromSeconds(3), "the unlock to be handled");
        AssertStillWorks(field);

        Native.PostMessageW(MessageWindow(), WmPowerBroadcast, PbtApmResumeAutomatic, 0);
        Wait.Until(() => _inlay.Log.Contains("resumed from sleep", StringComparison.Ordinal), TimeSpan.FromSeconds(3), "the resume to be handled");
        AssertStillWorks(field);
        Assert.DoesNotContain("[ERROR]", _inlay.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_display_change_closes_the_popup_and_typing_continues()
    {
        _inlay.Start();
        var field = Field();
        field.Focus();
        Keyboard.Type(":smi");
        _inlay.WaitForPopup();
        Native.PostMessageW(MessageWindow(), WmDisplayChange, 32, 0);
        _inlay.WaitForPopupClosed();
        AssertStillWorks(field);
    }

    [Fact]
    public void On_a_second_monitor_the_popup_opens_on_that_monitor()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var second = screens.FirstOrDefault(s => !s.Primary);
        if (second is null)
            Assert.Skip("Only one monitor.");

        _inlay.Start();
        var field = Field();
        var window = _edge!.Window.AsWindow();
        window.Move(second.WorkingArea.Left + 100, second.WorkingArea.Top + 100);
        Thread.Sleep(500);
        field.Focus();
        Keyboard.Type(":smi");
        var popup = _inlay.WaitForPopup();
        var area = second.WorkingArea;
        Assert.InRange(popup.Left, area.Left, area.Right);
        Assert.InRange(popup.Right, area.Left, area.Right);
        Assert.InRange(popup.Top, area.Top, area.Bottom);
        Assert.InRange(popup.Bottom, area.Top, area.Bottom);
        Keyboard.Press(Keyboard.VK_ESCAPE);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);
}
