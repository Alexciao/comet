using Inlay.E2E.Harness;

namespace Inlay.E2E;

/// <summary>Milestones 1 and 3 in Notepad: trigger rules, the popup at the caret, and replacement.</summary>
[Collection(Desktop.Name)]
public sealed class NotepadEmojiTests : IDisposable
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(3);

    private readonly InlayApp _inlay = new();
    private readonly NotepadTarget _notepad;

    public NotepadEmojiTests()
    {
        try
        {
            _inlay.Start();
            _notepad = new NotepadTarget();
        }
        catch
        {
            // xunit doesn't call Dispose when the constructor throws: don't leave Inlay running.
            _inlay.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _notepad.Dispose();
        _inlay.Dispose();
    }

    private string WaitForText(string expected) =>
        Wait.For(_notepad.ReadText, t => t == expected, s_settle, $"Notepad to read \"{expected}\"");

    [Fact]
    public void Full_code_with_closing_colon_is_replaced()
    {
        _notepad.Focus();
        Keyboard.Type("hi :smile:");
        Assert.Equal("hi 😄", WaitForText("hi 😄"));
    }

    [Fact]
    public void Popup_appears_next_to_the_caret_and_enter_inserts_the_first_result()
    {
        _notepad.Focus();
        Keyboard.Type("hey :thumbsu");
        var popup = _inlay.WaitForPopup();
        var field = _notepad.Bounds;
        Assert.InRange(popup.Left, field.Left, field.Right);
        Assert.InRange(popup.Top, field.Top, field.Top + 120); // just below the first line

        Keyboard.Press(Keyboard.VK_RETURN);
        _inlay.WaitForPopupClosed();
        Assert.Equal("hey 👍", WaitForText("hey 👍"));
    }

    [Fact]
    public void Arrow_keys_move_the_selection_and_tab_inserts()
    {
        _notepad.Focus();
        Keyboard.Type(":thumbs");
        _inlay.WaitForPopup();
        Keyboard.Press(Keyboard.VK_DOWN);
        Keyboard.Press(Keyboard.VK_UP);
        Keyboard.Press(Keyboard.VK_DOWN);
        Keyboard.Press(Keyboard.VK_TAB);
        // :thumbs → 👍 first, 👎 second.
        Assert.Equal("👎", WaitForText("👎"));
    }

    [Fact]
    public void Escape_closes_the_popup_and_keeps_the_typed_text()
    {
        _notepad.Focus();
        Keyboard.Type(":smi");
        _inlay.WaitForPopup();
        Keyboard.Press(Keyboard.VK_ESCAPE);
        _inlay.WaitForPopupClosed();
        Keyboard.Type("le");
        Assert.Equal(":smile", WaitForText(":smile"));
    }

    [Fact]
    public void Space_after_an_exact_code_inserts_it()
    {
        _notepad.Focus();
        Keyboard.Type(":fire done");
        Assert.Equal("🔥 done", WaitForText("🔥 done"));
    }

    [Theory]
    [InlineData("meet at 10:30 ok")]
    [InlineData("see https://example.com/a:b")]
    [InlineData("C:\\Users\\me")]
    [InlineData("nice :) and :D and :P")]
    [InlineData("ratio 3:2 and :x")]
    public void Times_urls_paths_and_emoticons_are_left_alone(string text)
    {
        _notepad.Focus();
        Keyboard.Type(text);
        Assert.Equal(text, WaitForText(text));
        Assert.Equal(0, _inlay.VisiblePopup());
    }

    [Fact]
    public void Skin_tones_family_and_flag_like_sequences_insert_intact()
    {
        _notepad.Focus();
        Keyboard.Type(":family_adult_adult_child: :heart_on_fire: :rainbow_flag:");
        Assert.Equal("🧑‍🧑‍🧒 ❤️‍🔥 🏳️‍🌈", WaitForText("🧑‍🧑‍🧒 ❤️‍🔥 🏳️‍🌈"));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Desktop
{
    public const string Name = "desktop";
}
