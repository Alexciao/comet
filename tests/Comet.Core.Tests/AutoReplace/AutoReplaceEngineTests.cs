using Comet.Core.AutoReplace;
using Comet.Core.Input;

namespace Comet.Core.Tests.AutoReplace;

public sealed class AutoReplaceEngineTests
{
    private static AutoReplaceEngine Engine(params AutoReplaceRule[] rules)
    {
        var e = new AutoReplaceEngine();
        e.SetRules(rules.Length > 0 ? rules : [new AutoReplaceRule("cmq", "comunque"), new AutoReplaceRule("pk", "perché")]);
        return e;
    }

    private static AutoReplaceDecision Type(AutoReplaceEngine e, string text)
    {
        AutoReplaceDecision last = default;
        foreach (var c in text)
        {
            last = c switch
            {
                '\n' => e.OnKey(KeyInput.Of(KeyKind.Enter)),
                '\t' => e.OnKey(KeyInput.Of(KeyKind.Tab)),
                _ => e.OnKey(KeyInput.Text(c)),
            };
        }

        return last;
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('!')]
    [InlineData('?')]
    [InlineData(')')]
    public void Expands_a_whole_word_at_a_boundary_and_keeps_the_boundary(char boundary)
    {
        var d = Type(Engine(), "cmq" + boundary);
        Assert.True(d.Swallow);
        Assert.Equal(AutoReplaceAction.Expand, d.Action);
        Assert.Equal(3, d.DeleteCount);
        Assert.Equal("comunque", d.Text);
        Assert.Equal(new Boundary(BoundaryKind.Char, boundary), d.Boundary);
    }

    [Fact]
    public void Enter_and_tab_are_held_and_resent()
    {
        var d = Type(Engine(), "cmq\n");
        Assert.True(d.Swallow);
        Assert.Equal(BoundaryKind.Enter, d.Boundary.Kind);
        Assert.Equal(BoundaryKind.Tab, Type(Engine(), "cmq\t").Boundary.Kind);
    }

    [Theory]
    [InlineData("xcmq ")]
    [InlineData("3cmq ")]
    [InlineData("cmqx ")]
    [InlineData("é" + "cmq ")]
    [InlineData("end.cmq ")]    // closing punctuation directly followed by letters is one token (A7)
    [InlineData("comet.cmq ")]
    [InlineData("3,cmq ")]
    public void Never_fires_inside_a_word_or_after_a_letter_or_digit(string text) =>
        Assert.Equal(AutoReplaceAction.None, Type(Engine(), text).Action);

    [Theory]
    [InlineData("(cmq ")]
    [InlineData("hello cmq ")]
    [InlineData("end. cmq ")]
    [InlineData("\"cmq\"")]
    [InlineData("'cmq ")]
    [InlineData("«cmq»")]
    public void Fires_after_whitespace_or_punctuation(string text) =>
        Assert.Equal(AutoReplaceAction.Expand, Type(Engine(), text).Action);

    [Theory]
    [InlineData("cmq", "comunque")]
    [InlineData("Cmq", "Comunque")]
    [InlineData("CMQ", "COMUNQUE")]
    [InlineData("cMq", "comunque")] // lowercase first letter, not all caps: as defined
    public void Case_follows_what_was_typed(string typed, string expected) =>
        Assert.Equal(expected, Type(Engine(), typed + " ").Text);

    [Fact]
    public void Accented_replacements_come_out_correctly_in_every_case()
    {
        Assert.Equal("perché", Type(Engine(), "pk ").Text);
        Assert.Equal("Perché", Type(Engine(), "Pk ").Text);
        Assert.Equal("PERCHÉ", Type(Engine(), "PK ").Text);
    }

    [Fact]
    public void Match_case_rules_fire_only_on_the_exact_spelling_and_keep_the_replacement_verbatim()
    {
        var e = Engine(new AutoReplaceRule("BTW", "by the way", MatchCase: true));
        Assert.Equal(AutoReplaceAction.None, Type(e, "btw ").Action);
        Assert.Equal("by the way", Type(e, "BTW ").Text);
    }

    [Fact]
    public void Backspace_right_after_an_expansion_undoes_it_once()
    {
        var e = Engine();
        Type(e, "cmq ");
        var undo = e.OnKey(KeyInput.Of(KeyKind.Backspace));
        Assert.True(undo.Swallow);
        Assert.Equal(AutoReplaceAction.Undo, undo.Action);
        Assert.Equal("comunque".Length + 1, undo.DeleteCount);
        Assert.Equal("cmq", undo.Text);
        Assert.Equal(new Boundary(BoundaryKind.Char, ' '), undo.Boundary);

        // A second Backspace is an ordinary key.
        Assert.Equal(AutoReplaceAction.None, e.OnKey(KeyInput.Of(KeyKind.Backspace)).Action);
    }

    [Fact]
    public void An_undone_word_does_not_expand_again_until_something_else_is_typed()
    {
        var e = Engine();
        Type(e, "cmq ");
        e.OnKey(KeyInput.Of(KeyKind.Backspace)); // undo → "cmq " typed back
        // The user erases the space and types it again: still not expanded.
        e.OnKey(KeyInput.Of(KeyKind.Backspace));
        Assert.Equal(AutoReplaceAction.None, Type(e, " ").Action);
        // Something else typed: expansion works again.
        Assert.Equal(AutoReplaceAction.Expand, Type(e, "ok cmq ").Action);
    }

    [Fact]
    public void Any_other_key_after_an_expansion_drops_the_undo()
    {
        var e = Engine();
        Type(e, "cmq ");
        Type(e, "x");
        Assert.Equal(AutoReplaceAction.None, e.OnKey(KeyInput.Of(KeyKind.Backspace)).Action);
    }

    [Fact]
    public void Backspace_edits_the_word()
    {
        var e = Engine();
        Type(e, "cmw");
        e.OnKey(KeyInput.Of(KeyKind.Backspace));
        Assert.Equal(AutoReplaceAction.Expand, Type(e, "q ").Action);
    }

    [Fact]
    public void Erasing_unseen_text_keeps_the_next_word_eligible()
    {
        // As after a click: the text before the caret is unknown, so the next word counts as a fresh word
        // (Ctrl+A, Backspace and then an abbreviation is the common case).
        var e = Engine();
        e.OnKey(KeyInput.Of(KeyKind.Backspace));
        Assert.Equal(AutoReplaceAction.Expand, Type(e, "cmq ").Action);
    }

    [Fact]
    public void Backspace_over_a_boundary_restores_the_previous_word()
    {
        var e = Engine();
        Type(e, "cm ");
        e.OnKey(KeyInput.Of(KeyKind.Backspace)); // erase the space: the caret is after "cm" again
        Assert.Equal(AutoReplaceAction.Expand, Type(e, "q ").Action);

        Type(e, "hello ");
        e.OnKey(KeyInput.Of(KeyKind.Backspace)); // back into "hello": "cmq" would be inside a word
        Assert.Equal(AutoReplaceAction.None, Type(e, "cmq ").Action);
    }

    [Fact]
    public void Not_allowed_contexts_expand_nothing()
    {
        var e = Engine();
        foreach (var c in "cmq")
            e.OnKey(KeyInput.Text(c), allowed: false);
        Assert.Equal(AutoReplaceAction.None, e.OnKey(KeyInput.Text(' '), allowed: false).Action);
    }

    [Fact]
    public void Reset_after_navigation_treats_the_next_word_as_fresh()
    {
        var e = Engine();
        Type(e, "xx");
        e.OnKey(KeyInput.Of(KeyKind.Navigation));
        Assert.Equal(AutoReplaceAction.Expand, Type(e, "cmq ").Action);
    }

    [Fact]
    public void Tracking_and_matching_do_not_allocate()
    {
        var e = Engine();
        Type(e, "hello world ");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Type(e, "hello world, this is a test of words that do not expand. ");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
