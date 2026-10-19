using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Inlay.App.UI.Settings;

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

    /// <summary>
    /// Lets the user pick a backup: an Inlay backup (<c>.inlay</c>), or a Comet 1.0 backup (<c>.json</c>, <c>.comet</c>).
    /// Returns its path, or null when cancelled.
    /// </summary>
    public static async Task<string?> OpenBackupAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(".inlay");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".comet");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Lets the user choose where to save a <c>.inlay</c> backup; returns the path, or null when cancelled.</summary>
    public static async Task<string?> SaveBackupAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id) { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("Inlay backup", [".inlay"]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or COMException;
}
