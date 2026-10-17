using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Comet.App.UI.Pages;

/// <summary>Helpers shared by the Snippets and Auto-replace pages.</summary>
internal static class PageHelpers
{
    /// <summary>One line of text, whitespace collapsed, for list previews (the card trims it with an ellipsis).</summary>
    public static string OneLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? trimmed : trimmed[..newline] + " …";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    public static async Task<string?> PickOpenAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickSaveAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id) { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public static bool Matches(string query, params string[] fields) =>
        query.Length == 0 || fields.Any(f => f.Contains(query, StringComparison.CurrentCultureIgnoreCase));
}
