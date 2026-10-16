using Comet.Core.Input;

namespace Comet.Core.Tests.Input;

public sealed class PickerSessionTests
{
    private sealed class Exact(params string[] codes) : IExactMatcher
    {
        public bool HasExact(ReadOnlySpan<char> token)
        {
            foreach (var c in codes)
            {
                if (token.SequenceEqual(c))
                    return true;
            }

            return false;
        }
    }

    private static PickerSession Session(params string[] exact) => new() { ExactMatcher = new Exact(exact) };

    /// <summary>Types text; returns the decision of the last key. Popup counts as visible whenever there's a query.</summary>
    private static PickerDecision Type(PickerSession s, string text)
    {
        PickerDecision last = default;
        foreach (var c in text)
            last = s.OnKey(KeyInput.Text(c), s.HasQuery);
        return last;
    }

    private static PickerDecision Press(PickerSession s, KeyKind kind, bool shift = false) => s.OnKey(KeyInput.Of(kind, shift), s.HasQuery);

    [Theory]
    [InlineData(":smi")]
    [InlineData("hello :smi")]
    [InlineData("(:smi")]
    [InlineData("\":smi")]
    [InlineData("a\t:smi")]
    public void Colon_after_start_whitespace_or_opening_punctuation_triggers(string text)
    {
        var s = Session();
        Type(s, text);
        Assert.True(s.HasQuery);
        Assert.Equal("smi", s.Token.ToString());
    }

    [Theory]
    [InlineData("10:30")]
    [InlineData("https://x")]
    [InlineData("C:\\Users")]
    [InlineData("note:sm")]
    [InlineData(":)")]
    [InlineData(":D ")]
    [InlineData(":DD")]
    [InlineData(":P")]
    [InlineData(":-)")]
    [InlineData(":-P")]
    [InlineData("::sm")]
    public void Times_urls_paths_and_emoticons_never_show_the_popup(string text)
    {
        var s = Session();
        Type(s, text);
        Assert.False(s.HasQuery);
    }

    [Fact]
    public void Plus_and_minus_one_are_valid_tokens()
    {
        var s = Session("+1", "-1");
        Type(s, ":+1");
        Assert.True(s.HasQuery);
        var d = Type(s, ":");
        Assert.Equal(PickerAction.CommitExact, d.Action);
        Assert.Equal(3, d.DeleteCount);
    }

    [Fact]
    public void Popup_needs_the_minimum_number_of_characters()
    {
        var s = Session();
        s.MinChars = 3;
        Type(s, ":sm");
        Assert.True(s.IsActive);
        Assert.False(s.HasQuery);
        Type(s, "i");
        Assert.True(s.HasQuery);
    }

    [Fact]
    public void Closing_colon_commits_an_exact_code_and_is_swallowed()
    {
        var s = Session("smile");
        var d = Type(s, ":smile:");
        Assert.True(d.Swallow);
        Assert.Equal(PickerAction.CommitExact, d.Action);
        Assert.Equal(6, d.DeleteCount); // ":smile"
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Closing_colon_without_exact_match_passes_through_and_ends_the_session()
    {
        var s = Session("smile");
        var d = Type(s, ":smil:");
        Assert.False(d.Swallow);
        Assert.Equal(PickerAction.Cancel, d.Action);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Codes_can_be_chained_right_after_an_insertion()
    {
        var s = Session("a1", "b2");
        Assert.Equal(PickerAction.CommitExact, Type(s, ":a1:").Action);
        s.NotifyInserted();
        Assert.Equal(PickerAction.CommitExact, Type(s, ":b2:").Action);
    }

    [Fact]
    public void Space_commits_only_an_exact_match()
    {
        var s = Session("smile");
        var d = Type(s, ":smile ");
        Assert.True(d.Swallow);
        Assert.Equal(PickerAction.CommitExactThenSpace, d.Action);

        d = Type(s, " :smil ");
        Assert.False(d.Swallow);
        Assert.Equal(PickerAction.Cancel, d.Action);
    }

    [Fact]
    public void Space_does_not_commit_a_token_shorter_than_the_minimum()
    {
        var s = Session("x");
        var d = Type(s, ":x ");
        Assert.False(d.Swallow);
        Assert.Equal(PickerAction.Cancel, d.Action);
    }

    [Fact]
    public void Navigation_keys_are_swallowed_only_while_the_popup_is_visible()
    {
        var s = Session();
        Type(s, ":smi");
        Assert.Equal(new PickerDecision(true, PickerAction.SelectNext), Press(s, KeyKind.Down));
        Assert.Equal(new PickerDecision(true, PickerAction.SelectPrevious), Press(s, KeyKind.Up));
        Assert.Equal(new PickerDecision(true, PickerAction.SelectPrevious), Press(s, KeyKind.Tab, shift: true));
        var enter = Press(s, KeyKind.Enter);
        Assert.True(enter.Swallow);
        Assert.Equal(PickerAction.CommitSelected, enter.Action);
        Assert.Equal(4, enter.DeleteCount);

        Type(s, " :s"); // below the minimum: no popup
        var down = Press(s, KeyKind.Down);
        Assert.False(down.Swallow);
        Assert.Equal(PickerAction.Cancel, down.Action);
    }

    [Fact]
    public void Tab_commits_like_enter()
    {
        var s = Session();
        Type(s, ":smi");
        var d = Press(s, KeyKind.Tab);
        Assert.True(d.Swallow);
        Assert.Equal(PickerAction.CommitSelected, d.Action);
    }

    [Fact]
    public void Escape_closes_and_is_swallowed_only_when_the_popup_was_open()
    {
        var s = Session();
        Type(s, ":smi");
        Assert.Equal(new PickerDecision(true, PickerAction.Cancel), Press(s, KeyKind.Escape));
        Assert.False(s.IsActive);
        Type(s, "le"); // keeps typing: no new session
        Assert.False(s.IsActive);
        Assert.Equal(default, Press(s, KeyKind.Escape));
    }

    [Theory]
    [InlineData(KeyKind.Navigation)]
    [InlineData(KeyKind.Shortcut)]
    [InlineData(KeyKind.Ime)]
    [InlineData(KeyKind.DeadKey)]
    public void Caret_moves_shortcuts_and_ime_cancel(KeyKind kind)
    {
        var s = Session();
        Type(s, ":smi");
        var d = Press(s, kind);
        Assert.False(d.Swallow);
        Assert.Equal(PickerAction.Cancel, d.Action);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Backspace_edits_the_token_and_erasing_the_colon_ends_the_session()
    {
        var s = Session();
        Type(s, ":smi");
        Press(s, KeyKind.Backspace);
        Assert.Equal("sm", s.Token.ToString());
        Press(s, KeyKind.Backspace);
        Press(s, KeyKind.Backspace);
        Assert.True(s.IsActive);
        Assert.Equal(PickerAction.Cancel, Press(s, KeyKind.Backspace).Action);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Reset_ends_the_session()
    {
        var s = Session();
        Type(s, ":smi");
        s.Reset();
        Assert.False(s.IsActive);
        Type(s, ":ok"); // after a reset the context is unknown, which allows a trigger
        Assert.True(s.HasQuery);
    }

    [Fact]
    public void Custom_trigger_character_is_honored()
    {
        var s = Session();
        s.TriggerChar = ';';
        Type(s, ":smi");
        Assert.False(s.IsActive);
        Type(s, " ;smi");
        Assert.True(s.HasQuery);
    }

    [Fact]
    public void Overlong_tokens_cancel()
    {
        var s = Session();
        Type(s, ":" + new string('a', PickerSession.MaxTokenLength));
        Assert.True(s.IsActive);
        Type(s, "a");
        Assert.False(s.IsActive);
    }
}
