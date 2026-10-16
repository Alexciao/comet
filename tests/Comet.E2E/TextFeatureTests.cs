using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>Comet with test-created snippets and auto-replacements, plus Notepad and Edge, shared by a test class.</summary>
public sealed class TextFeatureApps : IDisposable
{
    public TextFeatureApps()
    {
        Comet = new CometApp()
            .WithAutoReplace(("cmq", "comunque"), ("pk", "perché"))
            .WithSnippets(("website", "https://example.com/"), ("addr", "Via Roma 1\n{cursor}\nItalia"));
        try
        {
            Comet.Start();
            Notepad = new NotepadTarget();
            Edge = new EdgePage();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal CometApp Comet { get; }

    internal NotepadTarget? Notepad { get; }

    internal EdgePage? Edge { get; }

    internal ITextTarget Target(string name) => name switch
    {
        "notepad" => Notepad!,
        "textarea" => Edge!.Textarea,
        "rich" => Edge!.Rich,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public void Dispose()
    {
        Edge?.Dispose();
        Notepad?.Dispose();
        Comet.Dispose();
    }
}

/// <summary>Auto-replace and snippets in Notepad and in both Edge fields, with entries the test creates and removes.</summary>
[Collection(Desktop.Name)]
public sealed class TextFeatureTests(TextFeatureApps apps) : IClassFixture<TextFeatureApps>
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(4);

    public static TheoryData<string> Targets => ["notepad", "textarea", "rich"];

    private static string Expect(ITextTarget target, string expected) =>
        Wait.For(target.ReadText, t => t == expected, s_settle, $"{target.Name} to read \"{expected}\"");

    private ITextTarget Focus(string name)
    {
        var target = apps.Target(name);
        target.Focus();
        return target;
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Abbreviation_expands_on_space_period_and_enter(string name)
    {
        var t = Focus(name);
        Keyboard.Type("ok cmq va bene cmq.");
        Assert.Equal("ok comunque va bene comunque.", Expect(t, "ok comunque va bene comunque."));

        Keyboard.Type(" cmq\nnext");
        Assert.Equal("ok comunque va bene comunque. comunque\nnext", Expect(t, "ok comunque va bene comunque. comunque\nnext"));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Accents_and_case_follow_the_typed_word(string name)
    {
        var t = Focus(name);
        Keyboard.Type("pk Pk PK Cmq CMQ ");
        Assert.Equal("perché Perché PERCHÉ Comunque COMUNQUE ", Expect(t, "perché Perché PERCHÉ Comunque COMUNQUE "));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Backspace_right_after_an_expansion_undoes_it_and_inside_a_word_nothing_happens(string name)
    {
        var t = Focus(name);
        Keyboard.Type("cmq ");
        Expect(t, "comunque ");
        Keyboard.Press(Keyboard.VK_BACK);
        Assert.Equal("cmq ", Expect(t, "cmq "));
        Keyboard.Type("xcmq 3cmq ");
        Assert.Equal("cmq xcmq 3cmq ", Expect(t, "cmq xcmq 3cmq "));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Snippet_picked_from_the_list_inserts_its_text(string name)
    {
        var t = Focus(name);
        Keyboard.Type("site: :web");
        apps.Comet.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);
        Assert.Equal("site: https://example.com/", Expect(t, "site: https://example.com/"));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Multi_line_snippet_leaves_the_caret_at_cursor(string name)
    {
        var t = Focus(name);
        Keyboard.Type(":addr");
        apps.Comet.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);
        apps.Comet.WaitForPopupClosed();
        // Typing right away lands where {cursor} left the caret (keys typed during the insertion are held and replayed).
        Keyboard.Type("00100 Roma");
        Assert.Equal("Via Roma 1\n00100 Roma\nItalia", Expect(t, "Via Roma 1\n00100 Roma\nItalia"));
    }
}
