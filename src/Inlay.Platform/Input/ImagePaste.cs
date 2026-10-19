using System.Collections.Frozen;
using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Platform.Images;

namespace Inlay.Platform.Input;

/// <summary>An image insertion: what <see cref="InsertionJob.Image"/> carries.</summary>
/// <param name="Path">The file to insert (resolved by <c>ImagesService.ToInsertion</c>).</param>
/// <param name="Method">The clipboard formats for <see cref="ImageInsertAs.Image"/>: one of <see cref="ImagePasteMethod"/>.</param>
/// <param name="Process">The target app's process file name: its learned restore delay is kept under it.</param>
internal sealed record ImageInsertion(string Path, ImageInsertAs InsertAs, string Method, string Process)
{
    /// <summary>
    /// The file name the app should see ("lgtm.gif"), for library files (named by their hash): the file is offered
    /// through a hard link with this name. Null keeps the file's own name.
    /// </summary>
    public string? DisplayName { get; init; }

    public ImageType Type => ImageTypes.FromPath(Path);

    /// <summary>Set by the injector before the completion callback: what the app read during the paste.</summary>
    public ImagePasteResult? Result { get; set; }
}

/// <summary>What the target app did with an image paste.</summary>
/// <param name="FormatsRead">The formats it asked for, in order ("png", "dibv5", "hdrop"); empty when it read nothing.</param>
/// <param name="FirstReadMs">Milliseconds from Ctrl+V to the first read (-1 when nothing was read).</param>
internal sealed record ImagePasteResult(IReadOnlyList<string> FormatsRead, int FirstReadMs)
{
    public bool Read => FormatsRead.Count > 0;
}

/// <summary>A clipboard format an image is offered in.</summary>
internal enum ImageFormat : byte
{
    /// <summary><c>CF_HDROP</c>: the file itself. Keeps GIFs animated; chat apps upload it.</summary>
    File,
    /// <summary>The registered <c>PNG</c> format: lossless, with transparency.</summary>
    Png,
    /// <summary><c>CF_DIBV5</c> (Windows derives <c>CF_DIB</c> and <c>CF_BITMAP</c> from it on request).</summary>
    DibV5,
}

/// <summary>
/// The values of <c>AppSettings.ImagePasteMethods</c> (process name → method): which clipboard formats an image goes
/// in as for <see cref="ImageInsertAs.Image"/>. An app that reads nothing with one method gets the next one on the
/// following paste (<see cref="AfterUnread"/>); the engine stores it.
/// </summary>
internal static class ImagePasteMethod
{
    /// <summary>The file and the bitmap (PNG + DIBV5). For GIFs the file is offered first, so they stay animated.</summary>
    public const string Auto = "auto";

    /// <summary>The bitmap only: for apps that attach a file (<c>CF_HDROP</c>) instead of placing the image inline.</summary>
    public const string Bitmap = "bitmap";

    /// <summary>The file only.</summary>
    public const string File = "file";

    /// <summary>Apps whose behaviour is known: Office inlines a bitmap but turns a file into an attachment or an icon; Paint takes bitmaps only; Explorer pastes files.</summary>
    private static readonly FrozenDictionary<string, string> s_builtIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["outlook.exe"] = Bitmap,
        ["winword.exe"] = Bitmap,
        ["powerpnt.exe"] = Bitmap,
        ["excel.exe"] = Bitmap,
        ["onenote.exe"] = Bitmap,
        ["mspaint.exe"] = Bitmap,
        ["explorer.exe"] = File,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? method) => method is Auto or Bitmap or File;

    /// <summary>The method for an app: learned, else built in, else <see cref="Auto"/>.</summary>
    public static string For(string process, IReadOnlyDictionary<string, string>? learned) =>
        learned is not null && learned.TryGetValue(process, out var m) && IsValid(m) ? m
        : s_builtIn.TryGetValue(process, out var b) ? b
        : Auto;

    /// <summary>The method to try next after an app read nothing (auto → bitmap → file → auto).</summary>
    public static string AfterUnread(string method) => method switch
    {
        Auto => Bitmap,
        Bitmap => File,
        _ => Auto,
    };

    /// <summary>The formats put on the clipboard, in the order apps enumerate them. Empty for <see cref="ImageInsertAs.Path"/> (typed as text).</summary>
    public static IReadOnlyList<ImageFormat> Formats(ImageInsertion image) => image.InsertAs switch
    {
        ImageInsertAs.Path => [],
        ImageInsertAs.File => [ImageFormat.File],
        _ => image.Method switch
        {
            File => [ImageFormat.File],
            Bitmap => [ImageFormat.Png, ImageFormat.DibV5],
            _ when image.Type == ImageType.Gif => [ImageFormat.File, ImageFormat.Png, ImageFormat.DibV5], // a bitmap would arrive frozen
            _ => [ImageFormat.Png, ImageFormat.DibV5, ImageFormat.File],
        },
    };

    public static string Name(ImageFormat format) => format switch
    {
        ImageFormat.File => "hdrop",
        ImageFormat.Png => "png",
        _ => "dibv5",
    };
}

/// <summary>
/// The data of one image paste, rendered only when the app asks (delayed rendering): PNG from the file as it is when
/// it is a PNG, else decoded once and shared by PNG and DIBV5. Nothing is read from disk for a file-only paste.
/// </summary>
internal sealed class ImagePasteData(string path, string? offeredPath = null)
{
    private BgraImage? _decoded;
    private bool _decodeTried;

    /// <summary>The path in <c>CF_HDROP</c>: a hard link with the display name for library files, else the file itself.</summary>
    public string OfferedPath { get; } = offeredPath ?? path;

    public byte[]? Render(ImageFormat format)
    {
        try
        {
            return format switch
            {
                ImageFormat.File => DropFiles.Encode([OfferedPath]),
                ImageFormat.Png when ImageTypes.FromPath(path) == ImageType.Png => File.ReadAllBytes(path),
                ImageFormat.Png => Decoded() is { } image ? Png.Encode(image) : null,
                _ => Decoded() is { } image ? Dib.EncodeV5(image) : null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"image paste: can't read the file ({ex.GetType().Name})");
            return null;
        }
    }

    private BgraImage? Decoded()
    {
        if (!_decodeTried)
        {
            _decodeTried = true;
            _decoded = ImageCodec.Decode(path);
        }

        return _decoded;
    }

    /// <summary>
    /// A hard link (or, across volumes, a copy) of <paramref name="path"/> named <paramref name="displayName"/> in
    /// <c>%TEMP%\Inlay\paste</c>, so a chat app uploads "lgtm.gif" rather than the library's hash name. Links older
    /// than an hour are cleaned up here. Returns the path itself when staging isn't needed or fails.
    /// </summary>
    public static string Stage(string path, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(Path.GetFileName(path), displayName, StringComparison.OrdinalIgnoreCase)
            || displayName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return path;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Inlay", "paste");
            if (System.IO.Directory.Exists(root))
            {
                foreach (var old in new DirectoryInfo(root).EnumerateDirectories())
                {
                    try
                    {
                        if (old.CreationTimeUtc < DateTime.UtcNow.AddHours(-1))
                            old.Delete(recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // still open in an app: next time
                    }
                }
            }

            var dir = Path.Combine(root, Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture));
            System.IO.Directory.CreateDirectory(dir);
            var staged = Path.Combine(dir, displayName);
            if (!Native.ImageApi.CreateHardLinkW(staged, path, 0))
                File.Copy(path, staged);
            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"image paste: can't stage the file ({ex.GetType().Name}); offering it as it is");
            return path;
        }
    }
}
