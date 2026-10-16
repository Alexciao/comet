using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>Chromium: emoji in a textarea and a contenteditable, and nothing in a password field.</summary>
[Collection(Desktop.Name)]
public sealed class EdgeTests : IDisposable
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(4);

    private readonly CometApp _comet = new();
    private readonly EdgePage _edge;

    public EdgeTests()
    {
        try
        {
            _comet.Start();
            _edge = new EdgePage();
        }
        catch
        {
            _comet.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _edge.Dispose();
        _comet.Dispose();
    }

    private static string WaitForText(ITextTarget target, string expected) =>
        Wait.For(target.ReadText, t => t == expected, s_settle, $"{target.Name} to read \"{expected}\"");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closing_colon_and_popup_selection_insert_emoji(bool contentEditable)
    {
        var field = contentEditable ? _edge.Rich : _edge.Textarea;
        field.Focus();
        Keyboard.Type("hi :fire: and :thumbsu");
        var popup = _comet.WaitForPopup();
        var bounds = field.Bounds;
        Assert.InRange(popup.Left, bounds.Left - 10, bounds.Right);
        Assert.InRange(popup.Top, bounds.Top, bounds.Bottom + 40);
        Keyboard.Press(Keyboard.VK_RETURN);
        Assert.Equal("hi 🔥 and 👍", WaitForText(field, "hi 🔥 and 👍"));
    }

    [Fact]
    public void Password_fields_get_no_popup_and_no_replacement()
    {
        var field = _edge.Password;
        field.Focus();
        Keyboard.Type(":smile: :smi");
        Thread.Sleep(300); // give a (wrong) popup the time it would need to appear; asserting an absence
        Assert.Equal(0, _comet.VisiblePopup());
        Assert.Equal(":smile: :smi", field.ReadText());
    }
}
