using System.Text.Json.Nodes;
using Comet.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Comet.E2E;

/// <summary>
/// Drives every page, dialog and button of the Settings window through UI Automation and checks the result in the
/// saved files. Run it against the published build too (<c>COMET_EXE</c>): the first Native AOT build crashed on
/// Add, Edit and validation messages while the Debug build worked. Any crash log fails the test.
/// </summary>
[Collection(Desktop.Name)]
public sealed class SettingsTests : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(5);

    private readonly CometApp _comet = new();
    private AutomationElement _window = null!;

    public void Dispose() => _comet.Dispose();

    private void Open()
    {
        _comet.OpenSettings();
        _window = Automation.WaitForWindow(w => w.Name == "Comet Settings", TimeSpan.FromSeconds(10), "Settings");
    }

    private AutomationElement Find(string name, ControlType? type = null) =>
        Wait.For(
            () => _window.FindFirstDescendant(c => type is null ? c.ByName(name) : c.ByName(name).And(c.ByControlType(type.Value))),
            e => e is not null,
            s_wait,
            $"\"{name}\"")!;

    private void Page(string name)
    {
        Find(name, ControlType.ListItem).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Text).And(c.ByName(name))) is not null, s_wait, $"the {name} page");
    }

    /// <summary>The text field of a named control (an AutoSuggestBox names its container, not its inner TextBox).</summary>
    private AutomationElement EditIn(string name)
    {
        var e = Find(name);
        return e.ControlType == ControlType.Edit ? e : e.FindFirstDescendant(c => c.ByControlType(ControlType.Edit)) ?? e;
    }

    private static void Invoke(AutomationElement e) => e.Patterns.Invoke.Pattern.Invoke();

    private static void Toggle(AutomationElement e) => e.Patterns.Toggle.Pattern.Toggle();

    private static void SetText(AutomationElement e, string text) => e.Patterns.Value.Pattern.SetValue(text);

    /// <summary>A data file as Comet saved it; before the first save (or mid-write), an empty document.</summary>
    private JsonNode ReadJson(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(_comet.DataDirectory, file))) ?? Empty();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return Empty();
        }

        static JsonNode Empty() => new JsonObject { ["snippets"] = new JsonArray(), ["entries"] = new JsonArray(), ["features"] = new JsonObject(), ["autoReplaceExcludedApps"] = new JsonArray() };
    }

    private List<string?> Snippets() => [.. ReadJson("snippets.json")["snippets"]!.AsArray().Select(n => (string?)n!["name"])];

    private List<JsonNode> Replacements() => [.. ReadJson("autoreplace.json")["entries"]!.AsArray().Select(n => n!)];

    private void AssertNoCrash()
    {
        var crashes = Directory.Exists(Path.Combine(_comet.DataDirectory, "logs"))
            ? Directory.GetFiles(Path.Combine(_comet.DataDirectory, "logs"), "crash-*.log")
            : [];
        Assert.True(crashes.Length == 0, "Crash logs:\n" + string.Join("\n\n", crashes.Select(File.ReadAllText)));
        Assert.DoesNotContain("[FATAL]", _comet.Log, StringComparison.Ordinal);
    }

    /// <summary>Waits for a dialog with this title.</summary>
    private AutomationElement Dialog(string title) =>
        Wait.For(() => _window.FindFirstDescendant(c => c.ByControlType(ControlType.Window).And(c.ByName(title))) ?? _window.FindFirstDescendant(c => c.ByName(title)),
            e => e is not null, s_wait, $"the \"{title}\" dialog")!;

    [Fact]
    public void Every_page_dialog_and_button_works_without_crashing()
    {
        _comet.Start();
        Open();

        // ---- Snippets: add (with validation while typing), edit, duplicate, delete, undo.
        Page("Snippets");
        Invoke(Find("Add your first snippet", ControlType.Button));
        Dialog("Add snippet");
        var name = Find("Name", ControlType.Edit);
        SetText(name, "fire");                       // also an emoji shortcode: a warning
        SetText(name, "web site");                   // a space: an error, Save disabled
        Assert.False(Find("Save", ControlType.Button).IsEnabled);
        SetText(name, "website");
        SetText(Find("Text", ControlType.Edit), "https://example.com/ {date} {unknown}");
        Wait.Until(() => Find("Save", ControlType.Button).IsEnabled, s_wait, "Save to be enabled");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Snippets().SequenceEqual(["website"]), s_wait, "the snippet to be saved");

        Invoke(Find("Edit :website", ControlType.Button));
        Dialog("Edit snippet");
        SetText(Find("Name", ControlType.Edit), "site");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Snippets().SequenceEqual(["site"]), s_wait, "the edit to be saved");

        Invoke(Find("More options for :site", ControlType.Button));
        Invoke(Find("Duplicate", ControlType.MenuItem));
        Wait.Until(() => Snippets().Count == 2, s_wait, "the duplicate");

        Invoke(Find("More options for :site-copy", ControlType.Button));
        Invoke(Find("Delete", ControlType.MenuItem));
        Wait.Until(() => Snippets().SequenceEqual(["site"]), s_wait, "the delete");
        Invoke(Find("Undo", ControlType.Button));
        Wait.Until(() => Snippets().Count == 2, s_wait, "the undo");
        SetText(EditIn("Search snippets"), "copy");
        Wait.Until(() => _window.FindFirstDescendant(c => c.ByName("Edit :site")) is null, s_wait, "the search to filter");
        AssertNoCrash();

        // ---- Auto-replace: add, switch an entry off, edit.
        Page("Auto-replace");
        Invoke(Find("Add your first replacement", ControlType.Button));
        Dialog("Add replacement");
        SetText(Find("Abbreviation", ControlType.Edit), "c m q");   // an error while typing
        SetText(Find("Abbreviation", ControlType.Edit), "cmq");
        SetText(Find("Replace with", ControlType.Edit), "comunque");
        Wait.Until(() => Find("Save", ControlType.Button).IsEnabled, s_wait, "Save to be enabled");
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Replacements().Count == 1, s_wait, "the replacement to be saved");
        Toggle(Find("Use cmq  →  comunque"));
        Wait.Until(() => Replacements() is [var r] && (bool?)r["enabled"] == false, s_wait, "the entry to be switched off");
        Invoke(Find("Edit cmq  →  comunque", ControlType.Button));
        Dialog("Edit replacement");
        Toggle(Find("Match case exactly", ControlType.CheckBox));
        Invoke(Find("Save", ControlType.Button));
        Wait.Until(() => Replacements() is [var r] && (bool?)r["matchCase"] == true, s_wait, "the edit to be saved");
        AssertNoCrash();

        // ---- Excluded apps: add by typing, remove.
        Page("Excluded apps");
        var group = Find("Excluded from auto-replace");
        // Type the name and press Enter, as a person would (UI Automation's SetValue reaches the AutoSuggestBox's
        // own Text a dispatcher turn later, so pressing Add straight after it can race).
        var addBox = Wait.For(() => group.FindFirstDescendant(c => c.ByControlType(ControlType.Edit)), e => e is not null, s_wait, "the add box")!;
        Automation.Foreground(_window);
        addBox.Focus();
        Keyboard.Type("Notepad");
        Keyboard.Press(Keyboard.VK_RETURN);
        Wait.Until(() => (ReadJson("settings.json")["autoReplaceExcludedApps"]?.AsArray() ?? []).Any(a => (string?)a == "notepad.exe"), s_wait, "the app to be excluded");
        Invoke(Find("Remove notepad.exe", ControlType.Button));
        Wait.Until(() => !(ReadJson("settings.json")["autoReplaceExcludedApps"]?.AsArray() ?? []).Any(a => (string?)a == "notepad.exe"), s_wait, "the app to be removed");
        AssertNoCrash();

        // ---- General: switches, choices, reset confirmation, pause.
        Page("General");
        Toggle(Find("Emoji", ControlType.Button));
        Wait.Until(() => (bool?)ReadJson("settings.json")["features"]?["emoji"] == false, s_wait, "Emoji to be switched off");
        var minChars = Find("Show the list after", ControlType.ComboBox);
        minChars.Patterns.ExpandCollapse.Pattern.Expand();
        Find("3 letters", ControlType.ListItem).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => (int?)ReadJson("settings.json")["minimumCharacters"] == 3, s_wait, "the minimum to be saved");
        Invoke(Find("Reset", ControlType.Button));
        Invoke(Wait.For(() => _window.FindAllDescendants(c => c.ByName("Reset").And(c.ByControlType(ControlType.Button))), b => b.Length >= 2, s_wait, "the confirmation")[^1]);
        Invoke(Find("Excluded apps", ControlType.Button));
        Wait.Until(() => _window.FindFirstDescendant(c => c.ByName("Emoji and snippets")) is not null, s_wait, "the link card to open Excluded apps");
        AssertNoCrash();

        // ---- About, then close (hides) and reopen.
        Page("About");
        Find("Logs");
        _window.Patterns.Window.Pattern.Close();
        Wait.Until(() => Automation.Instance.GetDesktop().FindFirstChild(c => c.ByName("Comet Settings")) is null, s_wait, "the window to hide");
        Open();
        Find("About", ControlType.ListItem);
        AssertNoCrash();
        Assert.True(_comet.IsRunning);
    }
}
