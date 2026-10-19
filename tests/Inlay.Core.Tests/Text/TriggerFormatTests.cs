using Inlay.Core.Settings;
using Inlay.Core.Text;

namespace Inlay.Core.Tests.Text;

public sealed class TriggerFormatTests
{
    [Theory]
    [InlineData(":", "fire", ":fire", ":fire:")]
    [InlineData(";", "email", ";email", ";email;")]
    [InlineData("\\", "deg", "\\deg", "\\deg\\")]
    public void Codes_use_the_trigger_character(string trigger, string name, string code, string closed)
    {
        Assert.Equal(code, TriggerFormat.Code(trigger, name));
        Assert.Equal(closed, TriggerFormat.Closed(trigger, name));
    }

    [Fact]
    public void Copy_placeholders_are_filled() =>
        Assert.Equal("Type ; and a few letters to open Quick Insert", TriggerFormat.Fill("Type {trigger} and a few letters to open Quick Insert", ";"));

    [Fact]
    public void Validation_messages_show_the_trigger()
    {
        var messages = EntryValidation.ValidateSnippet("fire", "x", [], _ => true, ";");
        Assert.Contains(messages, m => m.Text.Contains(";fire", StringComparison.Ordinal) && !m.Text.Contains(":fire", StringComparison.Ordinal));
    }
}
