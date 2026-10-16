using Comet.Core.AutoReplace;
using Comet.Platform.Hook;
using Comet.Platform.Input;
using Comet.Platform.Native;

namespace Comet.Platform.Tests;

/// <summary>What an insertion looks like as <c>SendInput</c> events (nothing is sent).</summary>
public sealed class InputBatchTests
{
    private const uint Unicode = User32.KEYEVENTF_UNICODE, Up = User32.KEYEVENTF_KEYUP;

    /// <summary>The UTF-16 units typed with KEYEVENTF_UNICODE, from key-down events only.</summary>
    private static string Typed(InputBatch batch)
    {
        var chars = new List<char>();
        foreach (var i in batch.Inputs)
        {
            if ((i.u.ki.dwFlags & Unicode) != 0 && (i.u.ki.dwFlags & Up) == 0)
                chars.Add((char)i.u.ki.wScan);
        }

        return new string([.. chars]);
    }

    [Theory]
    [InlineData("🔥")] // surrogate pair
    [InlineData("👍🏽")] // skin tone modifier
    [InlineData("👨‍👩‍👧")] // ZWJ sequence
    [InlineData("❤️")] // variation selector
    [InlineData("🇮🇹")] // regional indicators
    [InlineData("perché")]
    public void Text_is_sent_as_every_utf16_unit_in_order_with_matching_key_ups(string text)
    {
        var batch = new InputBatch().Text(text);
        Assert.Equal(text, Typed(batch));
        Assert.Equal(text.Length * 2, batch.Count);
        var inputs = batch.Inputs.ToArray();
        for (var i = 0; i < inputs.Length; i += 2)
        {
            Assert.Equal(Unicode, inputs[i].u.ki.dwFlags);
            Assert.Equal(Unicode | Up, inputs[i + 1].u.ki.dwFlags);
            Assert.Equal(inputs[i].u.ki.wScan, inputs[i + 1].u.ki.wScan);
            Assert.Equal(0, inputs[i].u.ki.wVk);
        }
    }

    [Fact]
    public void Every_event_is_signed_so_the_hook_ignores_it()
    {
        var batch = new InputBatch().Backspaces(2).Text("a\nb").Boundary(new Boundary(BoundaryKind.Enter, '\0'));
        foreach (var i in batch.Inputs)
            Assert.Equal(InputSignature.Comet, i.u.ki.dwExtraInfo);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void Each_line_break_is_one_shift_enter(string text)
    {
        var inputs = new InputBatch().Text(text).Inputs.ToArray();
        var vks = inputs.Where(i => (i.u.ki.dwFlags & Unicode) == 0).Select(i => (i.u.ki.wVk, (i.u.ki.dwFlags & Up) != 0)).ToArray();
        Assert.Equal(
            [(VirtualKey.VK_LSHIFT, false), (VirtualKey.VK_RETURN, false), (VirtualKey.VK_RETURN, true), (VirtualKey.VK_LSHIFT, true)],
            vks.Select(v => ((int)v.wVk, v.Item2)).ToArray());
        Assert.Equal("ab", Typed(new InputBatch().Text(text)));
    }

    [Fact]
    public void Tabs_are_the_tab_key()
    {
        var inputs = new InputBatch().Text("\t").Inputs.ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i => Assert.Equal(VirtualKey.VK_TAB, i.u.ki.wVk));
    }

    [Fact]
    public void Backspaces_carry_scan_codes()
    {
        var inputs = new InputBatch().Backspaces(3).Inputs.ToArray();
        Assert.Equal(6, inputs.Length);
        Assert.All(inputs, i =>
        {
            Assert.Equal(VirtualKey.VK_BACK, i.u.ki.wVk);
            Assert.Equal(0x0E, i.u.ki.wScan);
        });
    }

    [Fact]
    public void A_space_boundary_is_typed_and_enter_and_tab_are_keys()
    {
        Assert.Equal(" ", Typed(new InputBatch().Boundary(new Boundary(BoundaryKind.Char, ' '))));
        Assert.Equal(VirtualKey.VK_RETURN, new InputBatch().Boundary(new Boundary(BoundaryKind.Enter, '\0')).Inputs[0].u.ki.wVk);
        Assert.Equal(VirtualKey.VK_TAB, new InputBatch().Boundary(new Boundary(BoundaryKind.Tab, '\0')).Inputs[0].u.ki.wVk);
    }
}
