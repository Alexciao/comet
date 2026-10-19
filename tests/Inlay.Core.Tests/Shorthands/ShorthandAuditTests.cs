using Inlay.Core.Shorthands;
using Inlay.Core.Input;

namespace Inlay.Core.Tests.Shorthands;

/// <summary>Regression tests for the Shorthands findings in AUDIT.md (A1–A12).</summary>
public sealed class ShorthandAuditTests
{
    private static ShorthandEngine Engine(params ShorthandRule[] rules)
    {
        var e = new ShorthandEngine();
        e.SetRules(rules.Length > 0 ? rules : [new ShorthandRule("cmq", "comunque")]);
        return e;
    }

    private static ShorthandDecision Type(ShorthandEngine e, string text)
    {
        ShorthandDecision last = default;
        foreach (var c in text)
            last = e.OnKey(c == '\b' ? KeyInput.Of(KeyKind.Backspace) : KeyInput.Text(c));
        return last;
    }

    [Fact]
    public void A3_no_undo_after_an_enter_or_tab_boundary()
    {
        var e = Engine();
        Type(e, "cmq");
        Assert.Equal(ShorthandAction.Expand, e.OnKey(KeyInput.Of(KeyKind.Enter)).Action);
        // The message may have been sent: Backspace is just Backspace, never "type cmq and press Enter again".
        Assert.Equal(ShorthandAction.None, e.OnKey(KeyInput.Of(KeyKind.Backspace)).Action);

        Type(e, " cmq");
        Assert.Equal(ShorthandAction.Expand, e.OnKey(KeyInput.Of(KeyKind.Tab)).Action);
        Assert.Equal(ShorthandAction.None, e.OnKey(KeyInput.Of(KeyKind.Backspace)).Action);
    }

    [Fact]
    public void A2_shift_enter_and_shift_tab_keep_their_shift()
    {
        var e = Engine();
        Type(e, "cmq");
        var d = e.OnKey(KeyInput.Of(KeyKind.Enter, shift: true));
        Assert.Equal(ShorthandAction.Expand, d.Action);
        Assert.True(d.Boundary.Shift);
        Assert.Equal(BoundaryKind.Enter, d.Boundary.Kind);

        Type(e, " cmq");
        Assert.True(e.OnKey(KeyInput.Of(KeyKind.Tab, shift: true)).Boundary.Shift);
    }

    [Fact]
    public void A6_after_an_expansion_the_word_before_the_caret_is_the_replacement()
    {
        var e = Engine();
        Type(e, "cmq ");
        e.OnKey(KeyInput.Of(KeyKind.Ignored)); // Caps Lock: cancels the undo, keeps the text
        Type(e, "\b");                          // erase the space: the caret is after "comunque"
        Assert.Equal("comunque", e.CurrentWord.ToString());
        Assert.Equal(ShorthandAction.None, Type(e, " ").Action);
    }

    [Theory]
    [InlineData("www.inlay.it ", "it", "Italia")]
    [InlineData("you're ", "re", "regarding")]
    [InlineData("l'altro ", "altro", "altra cosa")]
    [InlineData("3.5 ", "5", "cinque")]
    public void A7_no_expansion_inside_addresses_numbers_or_after_an_apostrophe_in_a_word(string text, string abbreviation, string replacement) =>
        Assert.Equal(ShorthandAction.None, Type(Engine(new ShorthandRule(abbreviation, replacement)), text).Action);

    [Fact]
    public void A8_backspacing_back_into_a_word_continues_that_word()
    {
        var e = Engine();
        Type(e, "hello, \b\b");
        Assert.Equal("hello", e.CurrentWord.ToString());
        Assert.Equal(ShorthandAction.None, Type(e, "cmq ").Action);
    }

    [Fact]
    public void A8_backspace_erases_a_whole_emoji()
    {
        var e = Engine();
        Type(e, "x😀\b");
        Assert.Equal("x", e.CurrentWord.ToString());
        Type(e, "\b");
        Assert.Equal(ShorthandAction.Expand, Type(e, "cmq ").Action);
    }

    [Fact]
    public void A9_capitalizes_the_first_letter_even_after_punctuation()
    {
        var e = Engine(new ShorthandRule("pq", "«perché»"));
        Assert.Equal("«Perché»", Type(e, "Pq ").Text);
    }

    [Theory]
    [InlineData("IIRC", "if I recall correctly")]   // typed as written: as written
    [InlineData("iirc", "if I recall correctly")]
    [InlineData("Iirc", "If I recall correctly")]
    public void A10_an_abbreviation_typed_as_written_gives_the_replacement_as_written(string typed, string expected) =>
        Assert.Equal(expected, Type(Engine(new ShorthandRule("IIRC", "if I recall correctly")), typed + " ").Text);

    [Fact]
    public void A11_no_expansion_right_after_the_picker_trigger()
    {
        // ":cmq" with no emoji called cmq: the picker cancels on the space, and shorthands leaves it alone too.
        Assert.Equal(ShorthandAction.None, Type(Engine(), ":cmq ").Action);
    }

    [Fact]
    public void Decisions_carry_the_exact_text_they_delete()
    {
        var e = Engine(new ShorthandRule("lol", "😂"));
        var expand = Type(e, "lol ");
        Assert.Equal("lol", expand.Deleted);
        var undo = e.OnKey(KeyInput.Of(KeyKind.Backspace));
        Assert.Equal(ShorthandAction.Undo, undo.Action);
        Assert.Equal("😂 ", undo.Deleted); // two UTF-16 units plus the space: what an editor's selection must cover
        Assert.Equal(2, undo.DeleteCount);  // what Backspace-based apps need: one per character as seen
    }

    [Fact]
    public void A1_the_engine_reports_state_while_an_undo_or_a_word_is_known()
    {
        var e = Engine();
        Assert.False(e.HasState);
        Type(e, "cmq ");
        Assert.True(e.HasState); // a click now must be noticed (it moves the caret away from the undo)
        e.Reset();
        Assert.False(e.HasState);
    }
}
