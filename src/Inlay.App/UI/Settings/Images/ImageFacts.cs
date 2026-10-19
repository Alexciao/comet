using Inlay.Core.Images;

namespace Inlay.App.UI.Settings.Images;

/// <summary>An image file's size in bytes and pixels, for the Edit dialog's preview and its large-file warning.</summary>
/// <param name="Bytes">-1 when the file can't be read.</param>
/// <param name="Width">0 when the header can't be read.</param>
internal readonly record struct ImageFacts(long Bytes, int Width, int Height)
{
    public static ImageFacts Unknown { get; } = new(-1, 0, 0);

    /// <summary>Reads the file's length and its header (64 KB at most). Call off the UI thread.</summary>
    public static ImageFacts Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var buffer = new byte[(int)Math.Min(file.Length, ImageHeader.BytesNeeded)];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ImageHeader.TryReadSize(buffer.AsSpan(0, read), out var w, out var h) ? new(file.Length, w, h) : new(file.Length, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }
    }
}
