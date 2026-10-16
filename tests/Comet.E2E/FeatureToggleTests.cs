using System.Text.Json.Nodes;
using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>
/// The three features can be turned on and off independently, take effect without a restart, and a disabled
/// feature costs nothing (no emoji data loaded; with everything off the hook is uninstalled).
/// </summary>
[Collection(Desktop.Name)]
public sealed class FeatureToggleTests : IDisposable
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(4);

    private readonly CometApp _comet = new CometApp()
        .WithAutoReplace(("cmq", "comunque"))
        .WithSnippets(("website", "https://example.com/"));

    private NotepadTarget? _notepad;

    public void Dispose()
    {
        _notepad?.Dispose();
        _comet.Dispose();
    }

    private void Start(bool emoji, bool snippets, bool autoReplace)
    {
        _comet.WithFeatures(emoji, snippets, autoReplace).Start();
        _notepad = new NotepadTarget();
        _notepad.Focus();
    }

    private string Expect(string expected) =>
        Wait.For(_notepad!.ReadText, t => t == expected, s_settle, $"Notepad to read \"{expected}\"");

    /// <summary>Types text that must stay exactly as typed, and checks no popup appeared on the way.</summary>
    private void TypeUntouched(string text)
    {
        var before = _notepad!.ReadText();
        Keyboard.Type(text);
        Assert.Equal(0, _comet.VisiblePopup());
        Assert.Equal(before + text, Expect(before + text));
    }

    private void SetFeatures(bool emoji, bool snippets, bool autoReplace)
    {
        var updates = Count("search data updated");
        _comet.UpdateSettings(s => s["features"] = new JsonObject { ["emoji"] = emoji, ["snippets"] = snippets, ["autoReplace"] = autoReplace });
        Wait.Until(() => Count("search data updated") > updates, TimeSpan.FromSeconds(5), "Comet to apply the edited settings.json");
    }

    private int Count(string logText) => _comet.Log.Split(logText).Length - 1;

    [Fact]
    public void Emoji_off_lists_only_snippets_and_never_loads_emoji_data()
    {
        Start(emoji: false, snippets: true, autoReplace: true);
        Assert.Contains("emoji not loaded", _comet.Log, StringComparison.Ordinal);
        TypeUntouched(":smile: :smi ");
        Keyboard.Type(":web");
        _comet.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);
        Assert.EndsWith("https://example.com/", Expect(":smile: :smi https://example.com/"), StringComparison.Ordinal);
    }

    [Fact]
    public void Snippets_off_lists_only_emoji()
    {
        Start(emoji: true, snippets: false, autoReplace: true);
        Keyboard.Type(":website:");
        // "website" is no longer a snippet and no emoji is called that: the code stays as typed.
        Assert.Equal(":website:", Expect(":website:"));
        Keyboard.Type(" :fire:");
        Assert.Equal(":website: 🔥", Expect(":website: 🔥"));
    }

    [Fact]
    public void Emoji_and_snippets_off_means_colon_is_not_a_trigger()
    {
        Start(emoji: false, snippets: false, autoReplace: true);
        TypeUntouched(":smile: :web :websi");
        Keyboard.Type(" cmq ");
        Assert.Equal(":smile: :web :websi comunque ", Expect(":smile: :web :websi comunque "));
    }

    [Fact]
    public void Auto_replace_off_leaves_abbreviations_alone()
    {
        Start(emoji: true, snippets: true, autoReplace: false);
        TypeUntouched("ok cmq ");
        Keyboard.Type(":fire:");
        Assert.Equal("ok cmq 🔥", Expect("ok cmq 🔥"));
    }

    [Fact]
    public void All_off_uninstalls_the_hook_and_typing_is_untouched()
    {
        Start(emoji: false, snippets: false, autoReplace: false);
        Assert.Contains("hook not needed", _comet.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("keyboard hook installed", _comet.Log, StringComparison.Ordinal);
        TypeUntouched(":smile: cmq :web ");
    }

    [Fact]
    public void Turning_features_off_and_on_applies_immediately_without_a_restart()
    {
        Start(emoji: true, snippets: true, autoReplace: true);
        Keyboard.Type(":fire: cmq ");
        Expect("🔥 comunque ");

        SetFeatures(emoji: false, snippets: false, autoReplace: false);
        Wait.Until(() => _comet.Log.Contains("keyboard hook removed", StringComparison.Ordinal), s_settle, "the hook to be removed");
        TypeUntouched(":fire: cmq ");

        SetFeatures(emoji: true, snippets: true, autoReplace: true);
        Wait.Until(() => _comet.Log.Split("keyboard hook installed").Length > 2, s_settle, "the hook to be installed again");
        Keyboard.Type(":fire: cmq ");
        Assert.Equal("🔥 comunque :fire: cmq 🔥 comunque ", Expect("🔥 comunque :fire: cmq 🔥 comunque "));
    }
}
