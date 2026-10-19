using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Comet.App.UI.Settings;

/// <summary>Open/save pickers for JSON exports and backups. File contents are read and written asynchronously.</summary>
internal static class FilePickers
{
    /// <summary>Lets the user pick a JSON file and returns its text, or null when cancelled.</summary>
    public static async Task<string?> OpenJsonAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await File.ReadAllTextAsync(file.Path).ConfigureAwait(true);
    }

    /// <summary>Lets the user choose where to save <paramref name="json"/>; returns the file name, or null when cancelled.</summary>
    public static async Task<string?> SaveJsonAsync(Window owner, string suggestedName, string json)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id) { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return null;
        await File.WriteAllTextAsync(file.Path, json).ConfigureAwait(true);
        return Path.GetFileName(file.Path);
    }

    public static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or COMException;
}
