using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Inlay.Core.Images;
using Inlay.Platform.Native;

namespace Inlay.Platform.Images;

/// <summary>A file in the library: its name there (<c>ImageEntry.File</c>), the original name (<c>SourceName</c>) and its size.</summary>
internal sealed record LibraryImage(string File, string SourceName, long Size);

/// <summary>
/// <c>&lt;data dir&gt;\Images</c>: copies of the user's single images, each named by its content hash
/// (<c>&lt;sha256&gt;.&lt;ext&gt;</c>), so an entry keeps working when the original moves and identical files are
/// stored once. Files are never changed once written; unreferenced ones go in <see cref="CollectGarbage"/>.
/// </summary>
internal sealed partial class ImageLibrary(string directory)
{
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "Images");

    public string Directory { get; } = directory;

    /// <summary>Copies an image file in (unless the same content is already there). Null when the type isn't supported.</summary>
    /// <exception cref="IOException">The file can't be read or the library can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Same.</exception>
    public LibraryImage? Add(string sourcePath)
    {
        var type = ImageTypes.FromPath(sourcePath);
        if (type == ImageType.None)
            return null;
        System.IO.Directory.CreateDirectory(Directory);
        var temp = Path.Combine(Directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            // One read: hash while copying, then the copy takes its name (or goes, when that content is already here).
            byte[] hash;
            long size;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    sha.AppendData(buffer.AsSpan(0, read));
                    target.Write(buffer.AsSpan(0, read));
                }

                hash = sha.GetHashAndReset();
                size = target.Length;
            }

            var name = FileName(hash, type);
            var final = Path.Combine(Directory, name);
            if (File.Exists(final))
                File.Delete(temp);
            else
                File.Move(temp, final);
            return new LibraryImage(name, Path.GetFileName(sourcePath), size);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Stores image bytes (a pasted bitmap encoded as PNG).</summary>
    public LibraryImage AddBytes(ReadOnlySpan<byte> data, ImageType type, string sourceName)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = FileName(SHA256.HashData(data), type);
        var final = Path.Combine(Directory, name);
        if (!File.Exists(final))
        {
            var temp = Path.Combine(Directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    f.Write(data);
                File.Move(temp, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                TryDelete(temp); // the same content arrived at the same moment
            }
        }

        return new LibraryImage(name, sourceName, data.Length);
    }

    /// <summary>The full path of a library file (no disk access; a path in <paramref name="file"/> is ignored).</summary>
    public string PathOf(string file) => Path.Combine(Directory, Path.GetFileName(file));

    /// <summary>
    /// Deletes library files no entry uses, and copies left half-written by a crash. Call after the entry list is
    /// saved (not while an undo could still bring an entry back). Returns how many files went.
    /// </summary>
    public int CollectGarbage(IEnumerable<string> inUse)
    {
        if (!System.IO.Directory.Exists(Directory))
            return 0;
        var keep = new HashSet<string>(inUse.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles())
        {
            var stale = file.Name.StartsWith('.') && file.Name.EndsWith(".tmp", StringComparison.Ordinal) && file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1);
            if (!stale && (!LibraryName().IsMatch(file.Name) || keep.Contains(file.Name)))
                continue;
            if (TryDelete(file.FullName))
                deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// Ctrl+V on the Images page: image files copied in Explorer (<c>CF_HDROP</c>), else a copied bitmap (PNG, or a
    /// DIB stored as PNG). Empty when the clipboard holds no image. Call off the UI thread (large files are hashed).
    /// </summary>
    public IReadOnlyList<LibraryImage> AddFromClipboard()
    {
        var (files, png) = ReadClipboard();
        var added = new List<LibraryImage>();
        foreach (var f in files)
        {
            if (Add(f) is { } image)
                added.Add(image);
        }

        if (added.Count == 0 && png is not null)
            added.Add(AddBytes(png, ImageType.Png, "Pasted image.png"));
        return added;
    }

    private static string FileName(byte[] hash, ImageType type) => $"{Convert.ToHexStringLower(hash)}.{ImageTypes.LibraryExtension(type)}";

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9a-f]{64}\.(png|jpg|gif|webp|bmp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LibraryName();

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The image files, or else a bitmap as PNG bytes. The clipboard is held only while copying the data out.</summary>
    private static unsafe (List<string> Files, byte[]? Png) ReadClipboard()
    {
        var opened = false;
        for (var i = 0; i < 10 && !(opened = User32.OpenClipboard(0)); i++)
            Thread.Sleep(20);
        if (!opened)
            return ([], null);
        byte[]? drop = null, png = null, dib = null;
        try
        {
            drop = Read(ImageApi.CF_HDROP);
            if (drop is null)
            {
                png = Read(User32.RegisterClipboardFormatW("PNG"));
                if (png is null || !Png.IsPng(png))
                    dib = Read(ImageApi.CF_DIBV5) ?? Read(ImageApi.CF_DIB);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }

        var files = drop is null ? [] : DropFiles.Decode(drop).Where(f => ImageTypes.IsSupported(f)).ToList();
        if (dib is not null && Dib.Decode(dib) is { } bitmap)
            png = Png.Encode(bitmap);
        return (files, png is not null && Png.IsPng(png) ? png : null);

        static byte[]? Read(uint format)
        {
            if (!User32.IsClipboardFormatAvailable(format))
                return null;
            var handle = User32.GetClipboardData(format);
            var p = handle == 0 ? 0 : Kernel32.GlobalLock(handle);
            if (p == 0)
                return null;
            try
            {
                var bytes = new byte[(int)Kernel32.GlobalSize(handle)];
                Marshal.Copy(p, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
    }
}
