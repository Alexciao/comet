using System.Security.Cryptography;
using Inlay.Core.Images;
using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

public sealed class ImageLibraryTests
{
    [Fact]
    public void Files_are_named_by_content_hash_and_stored_once()
    {
        using var src = new TempFolder();
        using var lib = new TempFolder();
        var library = new ImageLibrary(lib.Path);
        var a = TestImages.WritePng(src.File("LGTM final.png"));
        var copy = src.File("copy.PNG");
        File.Copy(a, copy);

        var first = library.Add(a)!;
        var second = library.Add(copy)!;
        Assert.Equal($"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a)))}.png", first.File);
        Assert.Equal(first.File, second.File);
        Assert.Equal("LGTM final.png", first.SourceName);
        Assert.Equal("copy.PNG", second.SourceName);
        Assert.Equal(new FileInfo(a).Length, first.Size);
        Assert.Single(Directory.GetFiles(lib.Path));

        // The entry keeps working when the original goes.
        File.Delete(a);
        Assert.True(File.Exists(library.PathOf(first.File)));
    }

    [Fact]
    public void Jpeg_is_stored_as_jpg_and_unsupported_files_are_refused()
    {
        using var src = new TempFolder();
        using var lib = new TempFolder();
        var library = new ImageLibrary(Path.Combine(lib.Path, "Images"));
        File.WriteAllBytes(src.File("photo.jpeg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        File.WriteAllText(src.File("notes.txt"), "x");
        Assert.EndsWith(".jpg", library.Add(src.File("photo.jpeg"))!.File, StringComparison.Ordinal);
        Assert.Null(library.Add(src.File("notes.txt")));
    }

    [Fact]
    public void Garbage_collection_removes_only_unreferenced_library_files()
    {
        using var src = new TempFolder();
        using var lib = new TempFolder();
        var library = new ImageLibrary(lib.Path);
        var keep = library.Add(TestImages.WritePng(src.File("a.png"), seed: 1))!;
        var drop = library.Add(TestImages.WritePng(src.File("b.png"), seed: 2))!;
        var bytes = library.AddBytes(Png.Encode(TestImages.Gradient(3, 3)), ImageType.Png, "Pasted image.png");
        File.WriteAllText(lib.File("readme.txt"), "not ours");
        var staleTemp = lib.File(".0123.tmp");
        File.WriteAllText(staleTemp, "");
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(2, library.CollectGarbage([keep.File, bytes.File.ToUpperInvariant()]));
        Assert.True(File.Exists(library.PathOf(keep.File)));
        Assert.True(File.Exists(library.PathOf(bytes.File)));
        Assert.False(File.Exists(library.PathOf(drop.File)));
        Assert.True(File.Exists(lib.File("readme.txt")));
        Assert.Equal(0, new ImageLibrary(Path.Combine(lib.Path, "missing")).CollectGarbage([]));
    }

    [Fact]
    public void Paths_never_leave_the_library() =>
        Assert.Equal(Path.Combine(@"C:\lib", "x.png"), new ImageLibrary(@"C:\lib").PathOf(@"..\..\Windows\x.png"));
}
