using System.Text.Json.Nodes;
using Inlay.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Inlay.E2E;

/// <summary>
/// Drives every page, dialog and button of the Settings window through UI Automation and checks the result in the
/// saved files. Run it against the published build too (<c>INLAY_EXE</c>): the first Native AOT build crashed on
/// Add, Edit and validation messages while the Debug build worked. Any crash log fails the test.
/// </summary>
[Collection(Desktop.Name)]
public sealed class SettingsTests : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(5);

    private readonly InlayApp _inlay = new();
    private AutomationElement _window = null!;

    public void Dispose() => _inlay.Dispose();

    private void Open()
    {
        _inlay.OpenSettings();
        _window = Automation.WaitForWindow(w => w.Name == "Inlay Settings", TimeSpan.FromSeconds(10), "Settings");
    }

    private void Close()
    {
        _window.Patterns.Window.Pattern.Close();
        Wait.Until(() => Automation.Instance.GetDesktop().FindFirstChild(c => c.ByName("Inlay Settings")) is null, s_wait, "the window to close");
    }

    private AutomationElement Find(string name, ControlType? type = null) =>
        Wait.For(
            () => _window.FindFirstDescendant(c => type is null ? c.ByName(name) : c.ByName(name).And(c.ByControlType(type.Value))),
            e => e is not null,
            s_wait,
            $"\"{name}\"")!;

    private void Page(string name)
    {
        var item = Find(name, ControlType.ListItem);
        try
        {
            item.Patterns.SelectionItem.Pattern.Select();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            item.Click(); // UIA refuses Select while the frame is still navigating: a real click works then
        }
        Wait.Until(() => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Text).And(c.ByName(name))) is not null, s_wait, $"the {name} page");
    }

    /// <summary>The text field of a named control (an AutoSuggestBox names its container, not its inner TextBox).</summary>
    private AutomationElement EditIn(string name)
    {
        var e = Find(name);
        return e.ControlType == ControlType.Edit ? e : e.FindFirstDescendant(c => c.ByControlType(ControlType.Edit)) ?? e;
    }

    private static void Invoke(AutomationElement e) => e.Patterns.Invoke.Pattern.Invoke();

    /// <summary>
    /// Activates a clickable card with the keyboard. The toolkit's SettingsCard advertises the UI Automation Invoke
    /// pattern but fails when it is used (toolkit 8.3 preview 2), so tests press Enter on the focused card, as a
    /// keyboard user would.
    /// </summary>
    private void PressCard(string name)
    {
        var card = Find(name, ControlType.Button);
        Automation.Foreground(_window);
        card.Focus();
        Keyboard.Press(Keyboard.VK_RETURN);
    }

    private static void Toggle(AutomationElement e) => e.Patterns.Toggle.Pattern.Toggle();

    private static void SetText(AutomationElement e, string text) => e.Patterns.Value.Pattern.SetValue(text);

    /// <summary>A data file as Inlay saved it; before the first save (or mid-write), an empty document.</summary>
    private JsonNode ReadJson(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(_inlay.DataDirectory, file))) ?? Empty();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return Empty();
        }

        static JsonNode Empty() => new JsonObject { ["snippets"] = new JsonArray(), ["entries"] = new JsonArray(), ["features"] = new JsonObject(), ["shorthandsExcludedApps"] = new JsonArray() };
    }

    private List<string?> Snippets() => [.. ReadJson("snippets.json")["snippets"]!.AsArray().Select(n => (string?)n!["name"])];

    private List<JsonNode> Replacements() => [.. ReadJson("shorthands.json")["entries"]!.AsArray().Select(n => n!)];

    private JsonNode Settings() => ReadJson("settings.json");

    private void AssertNoCrash()
    {
        var crashes = Directory.Exists(Path.Combine(_inlay.DataDirectory, "logs"))
            ? Directory.GetFiles(Path.Combine(_inlay.DataDirectory, "logs"), "crash-*.log")
            : [];
        Assert.True(crashes.Length == 0, "Crash logs:\n" + string.Join("\n\n", crashes.Select(File.ReadAllText)));
        Assert.DoesNotContain("[FATAL]", _inlay.Log, StringComparison.Ordinal);
    }

    /// <summary>Waits for a dialog with this title.</summary>
    private AutomationElement Dialog(string title) =>
        Wait.For(() => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Window).And(c.ByName(title))) ?? _window.FindFirstDescendant(c => c.ByName(title)),
            e => e is not null, s_wait, $"the \"{title}\" dialog")!;

    [Fact]
    public void Every_page_dialog_and_button_works_without_crashing()
    {
        _inlay.Start();
        Open();

        // ---- Snippets: add (with validation while typing), edit by clicking the card, duplicate, delete, undo, search.
        Page("Snippets");
        Invoke(Find("Add snippet", ControlType.Button));
        Dialog("Add snippet");
        var name = Find("Name", ControlType.Edit);
        SetText(name, "fire");                       // also an emoji shortcode: a warning
        SetText(name, "web site");                   // a space: an error under the field, Save disabled
        Find("Names can't contain spaces. Use - or _ instead.");
        Assert.False(Find("Save", ControlType.Button).IsEnabled);
        SetText(name, "website");
        SetText(Find("Text", ControlType.Edit), "https://example.com/ {cursor}{date} {unknown}");
        Wait.Until(() => Find("Save", ControlType.Button).IsEnabled, s_wait, "Save to be enabled");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Snippets().SequenceEqual(["website"]), s_wait, "the snippet to be saved");

        PressCard("website, a placeholder isn't written correctly"); // the {unknown} placeholder is read out with the name
        Dialog("Edit snippet");
        SetText(Find("Name", ControlType.Edit), "site");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Snippets().SequenceEqual(["site"]), s_wait, "the edit to be saved");

        Invoke(Find("More options for site", ControlType.Button));
        Invoke(Find("Duplicate", ControlType.MenuItem));
        Wait.Until(() => Snippets().Count == 2, s_wait, "the duplicate");

        Invoke(Find("More options for site-copy", ControlType.Button));
        Invoke(Find("Delete", ControlType.MenuItem));
        Wait.Until(() => Snippets().SequenceEqual(["site"]), s_wait, "the delete");
        Invoke(Find("Undo", ControlType.Button));
        Wait.Until(() => Snippets().Count == 2, s_wait, "the undo");
        SetText(EditIn("Search snippets"), "copy");
        Wait.Until(() => _window.FindFirstDescendant(c => c.ByName("More options for site")) is null, s_wait, "the search to filter");
        Find("1 of 2 snippets");
        AssertNoCrash();

        // ---- Shorthands: add, switch an entry off, edit by clicking the card, exclude an app and remove it.
        Page("Shorthands");
        Invoke(Find("Add shorthand", ControlType.Button));
        Dialog("Add shorthand");
        SetText(Find("Shorthand", ControlType.Edit), "c m q");   // an error while typing
        Find("Shorthands can't contain spaces");
        SetText(Find("Shorthand", ControlType.Edit), "cmq");
        SetText(Find("Expands to", ControlType.Edit), "comunque");
        Wait.Until(() => Find("Save", ControlType.Button).IsEnabled, s_wait, "Save to be enabled");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Replacements().Count == 1, s_wait, "the replacement to be saved");
        Toggle(Find("Use cmq"));
        Wait.Until(() => Replacements() is [var r] && (bool?)r["enabled"] == false, s_wait, "the entry to be switched off");
        Invoke(Find("More options for cmq", ControlType.Button));
        Invoke(Find("Edit", ControlType.MenuItem));
        Dialog("Edit shorthand");
        Toggle(Find("Match case exactly", ControlType.CheckBox));
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Replacements() is [var r] && (bool?)r["matchCase"] == true, s_wait, "the edit to be saved");

        Invoke(Find("Add app", ControlType.Button));
        Dialog("Add an app");
        var list = Find("Open apps", ControlType.List);
        var first = Wait.For(() => list.FindFirstChild(c => c.ByControlType(ControlType.ListItem)), e => e is not null, s_wait, "the open apps")!;
        first.Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Find("Add", ControlType.Button).IsEnabled, s_wait, "Add to be enabled");
        Invoke(Find("Add", ControlType.Button));
        Wait.Until(() => (Settings()["shorthandsExcludedApps"]?.AsArray() ?? []).Count == 1, s_wait, "the app to be excluded");
        Invoke(Wait.For(() => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Button).And(c.ByName("Remove", FlaUI.Core.Definitions.PropertyConditionFlags.MatchSubstring))), e => e is not null, s_wait, "the remove button")!);
        Wait.Until(() => (Settings()["shorthandsExcludedApps"]?.AsArray() ?? []).Count == 0, s_wait, "the app to be removed");
        AssertNoCrash();

        // ---- General: start with Windows, minimum characters, updates.
        Page("General");
        Toggle(Find("Start with Windows", ControlType.Button));
        Wait.Until(() => (bool?)Settings()["startAtLogin"] == true, s_wait, "Start with Windows to be switched on");
        Toggle(Find("Notify me about Inlay updates", ControlType.Button));
        Wait.Until(() => (bool?)Settings()["notifyAppUpdates"] == false, s_wait, "update notifications to be switched off");
        var about = Find("About Inlay");
        (about.Patterns.ExpandCollapse.PatternOrDefault ?? about.FindFirstDescendant(c => c.ByControlType(ControlType.Button))!.Patterns.ExpandCollapse.Pattern).Expand();
        Find("Open logs folder", ControlType.Button);
        Find("Source code", ControlType.Button);
        AssertNoCrash();

        // ---- Quick Insert: the page builds, minimum characters saves.
        Page("Quick Insert");
        Find("Minimum characters", ControlType.Spinner).Patterns.RangeValue.Pattern.SetValue(3);
        Wait.Until(() => (int?)Settings()["minimumCharacters"] == 3, s_wait, "the minimum to be saved");
        Find("Keyboard shortcut");
        AssertNoCrash();

        // ---- Images: the page and its Add menu.
        Page("Images");
        Find("Add image…");
        AssertNoCrash();

        // ---- Emoji: the feature switch and the skin tone.
        Page("Emoji");
        Toggle(Find("Emoji", ControlType.Button));
        Wait.Until(() => (bool?)Settings()["features"]?["emoji"] == false, s_wait, "Emoji to be switched off");
        Assert.StartsWith("Active · v", _window.FindFirstDescendant(c => c.ByAutomationId("Status"))!.Name, StringComparison.Ordinal);
        var tone = Find("Default skin tone", ControlType.ComboBox);
        tone.Patterns.ExpandCollapse.Pattern.Expand();
        Find("✋🏽  Medium", ControlType.ListItem).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => (int?)Settings()["skinTone"] == 3, s_wait, "the skin tone to be saved");

        // ---- Closing destroys the window; it comes back on the page it was on, and closed windows don't pile up.
        for (var i = 0; i < 3; i++)
        {
            Close();
            Wait.Until(() => _inlay.Log.Split("settings released").Length - 1 > i, TimeSpan.FromSeconds(15), "the window to be released");
            Open();
            Find("Default skin tone", ControlType.ComboBox);
        }

        Assert.DoesNotContain("closed Settings windows are still in memory", _inlay.Log, StringComparison.Ordinal);
        AssertNoCrash();
        Assert.True(_inlay.IsRunning);
    }
}
