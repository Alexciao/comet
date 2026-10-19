using System.Runtime.InteropServices;
using Inlay.Platform.Native;

namespace Inlay.Platform.Images;

/// <summary>
/// <c>{Screenshots}</c>, <c>{CameraRoll}</c> and <c>{Pictures}</c> in <c>ImageEntry.Folder</c>, resolved with
/// SHGetKnownFolderPath each time, so a folder redirected to OneDrive (or moved in its Location tab) is followed.
/// </summary>
internal static class KnownFolders
{
    private static readonly (string Token, Guid Id)[] s_folders =
    [
        ("{Screenshots}", new("b7bede81-df94-4682-a7d8-57a52620b86f")),
        ("{CameraRoll}", new("ab5fb87b-7ce2-4f83-915d-550846c9537b")),
        ("{Pictures}", new("33e28130-4e1e-4676-835a-98395c3bc3bb")),
    ];

    public static bool IsToken(string? folder) => folder is not null && s_folders.Any(f => string.Equals(f.Token, folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder's full path (a known folder resolved, a path normalised), or null when it can't be resolved.</summary>
    public static string? Resolve(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        foreach (var (token, id) in s_folders)
        {
            if (string.Equals(token, folder, StringComparison.OrdinalIgnoreCase))
                return KnownFolderPath(id);
        }

        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim()));
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? KnownFolderPath(Guid id)
    {
        if (ImageApi.SHGetKnownFolderPath(id, ImageApi.KF_FLAG_DONT_VERIFY, 0, out var p) < 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(p) is { Length: > 0 } path ? Path.TrimEndingDirectorySeparator(path) : null;
        }
        finally
        {
            ImageApi.CoTaskMemFree(p);
        }
    }
}
