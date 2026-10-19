using Inlay.Core.Input;

namespace Inlay.Core.Tests.Input;

/// <summary>Recents after a pause, and the keyboard shortcut's session.</summary>
public sealed class QuickInsertModesTests
{
    private static QuickInsertDecision Type(QuickInsertSession s, string text, bool visible = true)
    {
        QuickInsertDecision last = default;
        foreach (var c in text)
            last = s.OnKey(KeyInput.Text(c), visible);
        return last;
    }

    private static QuickInsertDecision Press(QuickInsertSession s, KeyKind kind, bool visible = true, bool shift = false) => s.OnKey(KeyInput.Of(kind, shift), visible);

    // ---- recents on pause: the timer is armed only while AwaitingQuery

    [Fact]
    public void Awaiting_a_query_only_right_after_a_trigger()
    {
        var s = new QuickInsertSession();
        Assert.False(s.AwaitingQuery);
        Type(s, " :", visible: false);
        Assert.True(s.AwaitingQuery);
        Type(s, "f", visible: false);
        Assert.False(s.AwaitingQuery); // the next key disarms it
    }

    [Theory]
    [InlineData("Note:")] // colon right after a letter
    [InlineData("10:")]
    [InlineData("https:")]
    public void Normal_trigger_rules_still_apply(string text)
    {
        var s = new QuickInsertSession();
        Type(s, text, visible: false);
        Assert.False(s.AwaitingQuery);
    }

    [Fact]
    public void After_recents_open_one_letter_searches()
    {
        var s = new QuickInsertSession { MinChars = 2 };
        Type(s, ":", visible: false);
        s.OpenedEarly();
        Type(s, "f");
        Assert.True(s.HasQuery);
    }

    [Fact]
    public void Without_recents_the_minimum_still_applies()
    {
        var s = new QuickInsertSession { MinChars = 2 };
        Type(s, ":f", visible: false);
        Assert.False(s.HasQuery);
    }

    [Fact]
    public void A_new_session_forgets_the_early_open()
    {
        var s = new QuickInsertSession { MinChars = 2 };
        Type(s, ":", visible: false);
        s.OpenedEarly();
        Press(s, KeyKind.Escape);
        Type(s, " :f", visible: false);
        Assert.False(s.HasQuery);
    }

    [Theory]
    [InlineData(KeyKind.Escape, true)]
    public void Esc_closes_recents(KeyKind kind, bool swallowed)
    {
        var s = new QuickInsertSession();
        Type(s, ":", visible: false);
        s.OpenedEarly();
        var d = Press(s, kind);
        Assert.Equal((QuickInsertAction.Cancel, swallowed), (d.Action, d.Swallow));
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Space_closes_recents_and_reaches_the_app()
    {
        var s = new QuickInsertSession();
        Type(s, ":", visible: false);
        s.OpenedEarly();
        var d = Type(s, " ");
        Assert.Equal((QuickInsertAction.Cancel, false), (d.Action, d.Swallow));
    }

    [Fact]
    public void Enter_on_recents_inserts_and_erases_the_trigger()
    {
        var s = new QuickInsertSession();
        Type(s, ":", visible: false);
        s.OpenedEarly();
        var d = Press(s, KeyKind.Enter);
        Assert.Equal((QuickInsertAction.CommitSelected, true, 1), (d.Action, d.Swallow, d.DeleteCount));
    }

    // ---- the keyboard shortcut

    [Fact]
    public void Shortcut_keeps_every_query_key_from_the_app()
    {
        var s = new QuickInsertSession();
        s.StartFromShortcut();
        Assert.True(s.IsActive && s.FromShortcut);
        Assert.False(s.HasQuery); // recents until something is typed

        var d = Type(s, "Fi");
        Assert.Equal((QuickInsertAction.QueryChanged, true), (d.Action, d.Swallow));
        Assert.Equal("Fi", s.Token.ToString()); // capitals search too (no emoticon rule)
        Assert.True(s.HasQuery);

        Assert.True(Press(s, KeyKind.Backspace).Swallow);
        Assert.True(Press(s, KeyKind.Backspace).Swallow);
        var empty = Press(s, KeyKind.Backspace);
        Assert.Equal((QuickInsertAction.None, true), (empty.Action, empty.Swallow)); // nothing left: still open
        Assert.True(s.IsActive);
    }

    [Fact]
    public void Shortcut_inserts_without_erasing_anything()
    {
        var s = new QuickInsertSession();
        s.StartFromShortcut();
        Type(s, "deg");
        var d = Press(s, KeyKind.Enter);
        Assert.Equal((QuickInsertAction.CommitSelected, true, 0), (d.Action, d.Swallow, d.DeleteCount));
        Assert.Equal("deg", s.CommittedToken.ToString());
        Assert.False(s.IsActive || s.FromShortcut);
    }

    [Fact]
    public void Shortcut_navigation_and_escape_stay_in_quick_insert()
    {
        var s = new QuickInsertSession();
        s.StartFromShortcut();
        Assert.Equal((QuickInsertAction.SelectNext, true), (Press(s, KeyKind.Down).Action, Press(s, KeyKind.Down).Swallow));
        Assert.Equal(QuickInsertAction.SelectPrevious, Press(s, KeyKind.Up).Action);
        var esc = Press(s, KeyKind.Escape);
        Assert.Equal((QuickInsertAction.Cancel, true), (esc.Action, esc.Swallow));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('.')]
    [InlineData(':')]
    public void Shortcut_other_characters_close_it_and_reach_the_app(char c)
    {
        var s = new QuickInsertSession();
        s.StartFromShortcut();
        var d = s.OnKey(KeyInput.Text(c), true);
        Assert.Equal((QuickInsertAction.Cancel, false), (d.Action, d.Swallow));
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Shortcut_shortcuts_close_it_and_reach_the_app()
    {
        var s = new QuickInsertSession();
        s.StartFromShortcut();
        var d = Press(s, KeyKind.Shortcut);
        Assert.Equal((QuickInsertAction.Cancel, false), (d.Action, d.Swallow));
    }

    [Theory]
    [InlineData("email", true)]
    [InlineData("my-sig_2", true)]
    [InlineData("+1", true)]
    [InlineData("a b", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void Valid_names_for_create_snippet(string name, bool valid) =>
        Assert.Equal(valid, QuickInsertSession.IsValidName(name));
}
