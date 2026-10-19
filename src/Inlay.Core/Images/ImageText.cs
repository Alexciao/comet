using System.Globalization;
using Inlay.Core.Settings;

namespace Inlay.Core.Images;

/// <summary>Secondary text for image rows in Quick Insert and Settings ("GIF · 1.2 MB", "Newest in Screenshots · 2 min ago").</summary>
public static class ImageText
{
    private const string Dot = " · ";

    /// <summary>Known folders as stored in <c>ImageEntry.Folder</c>, with their display names.</summary>
    public static IReadOnlyList<(string Token, string Name)> KnownFolders { get; } =
    [
        ("{Screenshots}", "Screenshots"),
        ("{CameraRoll}", "Camera Roll"),
        ("{Pictures}", "Pictures"),
    ];

    /// <summary>"GIF · 1.2 MB".</summary>
    public static string Describe(ImageType type, long size, IFormatProvider? culture = null) =>
        size < 0 ? ImageTypes.DisplayName(type) : ImageTypes.DisplayName(type) + Dot + Size(size, culture);

    /// <summary>"512 bytes", "340 KB", "1.2 MB", "25 MB".</summary>
    public static string Size(long bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => string.Format(culture, "{0} bytes", bytes),
            < 1024 * 1024 => string.Format(culture, "{0:0} KB", Math.Max(1, bytes / Kb)),
            < 10 * 1024 * 1024 => string.Format(culture, "{0:0.0} MB", bytes / Mb),
            < 1024L * 1024 * 1024 => string.Format(culture, "{0:0} MB", bytes / Mb),
            _ => string.Format(culture, "{0:0.0} GB", bytes / Gb),
        };
    }

    /// <summary>"Screenshots" for <c>{Screenshots}</c>, the last part of a path otherwise.</summary>
    public static string FolderName(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "";
        foreach (var (token, name) in KnownFolders)
        {
            if (string.Equals(folder, token, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        var trimmed = folder.TrimEnd('\\', '/');
        var name2 = Path.GetFileName(trimmed);
        return name2.Length > 0 ? name2 : trimmed;
    }

    /// <summary>
    /// A Folder entry's row: "Newest in Screenshots · 2 min ago", "Random from Cats", "Pick from Screenshots".
    /// <paramref name="newest"/> is the local time of the newest image (null when unknown or the folder is empty).
    /// </summary>
    public static string Folder(FolderMode mode, string folderName, DateTime? newest, DateTime now, IFormatProvider? culture = null) => mode switch
    {
        FolderMode.Newest => newest is { } t ? $"Newest in {folderName}{Dot}{Relative(t, now, culture)}" : $"Newest in {folderName}",
        FolderMode.Random => $"Random from {folderName}",
        _ => $"Pick from {folderName}",
    };

    /// <summary>A collection's Settings card: "Collection · Reactions · 24 images".</summary>
    public static string Collection(string folderName, int count, IFormatProvider? culture = null) =>
        string.Format(culture ?? CultureInfo.CurrentCulture, "Collection{0}{1}{0}{2} {3}", Dot, folderName, count, count == 1 ? "image" : "images");

    /// <summary>
    /// "just now", "2 min ago", "3 h ago", "yesterday", then the date ("12 Oct", with the year when it isn't this
    /// year). Both times are local.
    /// </summary>
    public static string Relative(DateTime then, DateTime now, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var ago = now - then;
        if (ago < TimeSpan.FromMinutes(1))
            return "just now"; // also a clock that moved back
        if (ago < TimeSpan.FromHours(1))
            return string.Format(culture, "{0} min ago", (int)ago.TotalMinutes);
        if (then.Date == now.Date || ago < TimeSpan.FromHours(6))
            return string.Format(culture, "{0} h ago", (int)ago.TotalHours);
        if (then.Date == now.Date.AddDays(-1))
            return "yesterday";
        return then.ToString(then.Year == now.Year ? "d MMM" : "d MMM yyyy", culture);
    }
}
