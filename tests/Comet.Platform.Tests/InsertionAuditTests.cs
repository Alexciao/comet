using Comet.Core.AutoReplace;
using Comet.Platform.Input;
using Comet.Platform.Native;

namespace Comet.Platform.Tests;

/// <summary>Regression tests for the insertion findings in AUDIT.md (U1, U2, U10, A2).</summary>
public sealed class InsertionAuditTests
{
    private static InsertionJob Job(Boundary? after = null) => new(0, null, null, after, InsertionMethod.Unicode, 0);

    [Fact]
    public void U1_undoing_an_emoji_replacement_selects_whole_surrogate_pairs()
    {
        // Notepad holds "😂 " before the caret (index 3): undo must replace all three UTF-16 units, not two.
        var deleted = "😂 ";
        var (start, insert, _) = TextInjector.PlanDirectEdit(end: 3, deleteUnits: deleted.Length, "lol", "", new Boundary(BoundaryKind.Char, ' '));
        Assert.Equal(0, start);
        Assert.Equal("lol ", insert);
    }

    [Theory]
    [InlineData("<b></b>", "</b>", ' ', 3)]                     // caret between the tags; the commit space at the end
    [InlineData("Dear X,\r\n\r\nRegards", "\r\nRegards", ' ', 8)] // CRLF counts as one \r in the editor
    public void U2_the_cursor_lands_where_the_snippet_put_it(string text, string afterCursor, char boundary, int expectedCursor)
    {
        var (start, insert, cursor) = TextInjector.PlanDirectEdit(end: 10, deleteUnits: 10, text, afterCursor, new Boundary(BoundaryKind.Char, boundary));
        Assert.Equal(0, start);
        Assert.Equal(expectedCursor, cursor);
        Assert.EndsWith(boundary.ToString(), insert, StringComparison.Ordinal);
        Assert.Equal(afterCursor.Replace("\r\n", "\r", StringComparison.Ordinal) + boundary, insert[expectedCursor..]);
    }

    [Fact]
    public void U10_typed_snippets_move_the_caret_back_over_the_commit_space_too()
    {
        Assert.Equal(5, TextInjector.CaretBack(Job(new Boundary(BoundaryKind.Char, ' ')), "</b>"));
        Assert.Equal(4, TextInjector.CaretBack(Job(), "</b>"));
        Assert.Equal(0, TextInjector.CaretBack(Job(new Boundary(BoundaryKind.Char, ' ')), ""));
    }

    [Fact]
    public void A2_shift_enter_is_sent_with_shift()
    {
        var batch = new InputBatch().Boundary(new Boundary(BoundaryKind.Enter, '\n', Shift: true));
        var keys = batch.Inputs.ToArray().Select(i => (i.u.ki.wVk, Up: (i.u.ki.dwFlags & User32.KEYEVENTF_KEYUP) != 0)).ToArray();
        Assert.Equal([(VirtualKey.VK_LSHIFT, false), (VirtualKey.VK_RETURN, false), (VirtualKey.VK_RETURN, true), (VirtualKey.VK_LSHIFT, true)], keys.Select(k => ((int)k.wVk, k.Up)));
        Assert.Equal(2, new InputBatch().Boundary(new Boundary(BoundaryKind.Enter, '\n')).Count); // plain Enter: just Enter
    }
}
