namespace Inlay.Core.Images;

/// <summary>The image file types Inlay inserts. A set of them is an entry's file-type filter.</summary>
[Flags]
public enum ImageType : byte
{
    None = 0,
    Png = 1,
    Jpeg = 2,
    Gif = 4,
    WebP = 8,
    Bmp = 16,
    All = Png | Jpeg | Gif | WebP | Bmp,
}

public static class ImageTypes
{
    /// <summary>Lower-case extensions without the dot, as stored in <c>ImageEntry.FileTypes</c>.</summary>
    public static IReadOnlyList<string> Extensions { get; } = ["png", "jpg", "jpeg", "gif", "webp", "bmp"];

    /// <summary>The type of a file name or path by its extension; <see cref="ImageType.None"/> when unsupported.</summary>
    public static ImageType FromPath(ReadOnlySpan<char> path) => FromExtension(Path.GetExtension(path));

    /// <summary>The type for an extension, with or without the dot, any case.</summary>
    public static ImageType FromExtension(ReadOnlySpan<char> extension)
    {
        if (extension.StartsWith("."))
            extension = extension[1..];
        return extension.Length switch
        {
            3 when extension.Equals("png", StringComparison.OrdinalIgnoreCase) => ImageType.Png,
            3 when extension.Equals("jpg", StringComparison.OrdinalIgnoreCase) => ImageType.Jpeg,
            4 when extension.Equals("jpeg", StringComparison.OrdinalIgnoreCase) => ImageType.Jpeg,
            3 when extension.Equals("gif", StringComparison.OrdinalIgnoreCase) => ImageType.Gif,
            4 when extension.Equals("webp", StringComparison.OrdinalIgnoreCase) => ImageType.WebP,
            3 when extension.Equals("bmp", StringComparison.OrdinalIgnoreCase) => ImageType.Bmp,
            _ => ImageType.None,
        };
    }

    public static bool IsSupported(ReadOnlySpan<char> path) => FromPath(path) != ImageType.None;

    /// <summary>An entry's <c>FileTypes</c> as a set; an empty list (or nothing recognised) means every type.</summary>
    public static ImageType Filter(IEnumerable<string>? fileTypes)
    {
        var filter = ImageType.None;
        foreach (var t in fileTypes ?? [])
            filter |= FromExtension(t.AsSpan().Trim());
        return filter == ImageType.None ? ImageType.All : filter;
    }

    /// <summary>The extension a library file of this type gets ("jpeg" files are stored as "jpg").</summary>
    public static string LibraryExtension(ImageType type) => type switch
    {
        ImageType.Png => "png",
        ImageType.Jpeg => "jpg",
        ImageType.Gif => "gif",
        ImageType.WebP => "webp",
        ImageType.Bmp => "bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The name shown in Quick Insert rows ("GIF", "PNG", "JPEG", "WebP", "BMP").</summary>
    public static string DisplayName(ImageType type) => type switch
    {
        ImageType.Png => "PNG",
        ImageType.Jpeg => "JPEG",
        ImageType.Gif => "GIF",
        ImageType.WebP => "WebP",
        ImageType.Bmp => "BMP",
        _ => "Image",
    };
}
