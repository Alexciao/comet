using Inlay.Core.Images;
using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

public sealed class FolderCacheTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static string[] Names(FolderCache cache) => [.. cache.Index.Files.Select(f => f.Name)];

    [Fact]
    public async Task The_first_scan_lists_images_newest_first_in_the_background()
    {
        using var dir = new TempFolder();
        TestImages.WritePng(dir.File("old.png"), lastWriteUtc: T0.AddHours(-2));
        TestImages.WritePng(dir.File("new.png"), lastWriteUtc: T0);
        File.WriteAllText(dir.File("notes.txt"), "x");
        Directory.CreateDirectory(dir.File("sub"));
        TestImages.WritePng(dir.File(@"sub\deep.png"), lastWriteUtc: T0.AddHours(-1));
        var hidden = TestImages.WritePng(dir.File("hidden.png"));
        File.SetAttributes(hidden, FileAttributes.Hidden);

        using var flat = new FolderCache(dir.Path, recursive: false, watch: false);
        using var deep = new FolderCache(dir.Path, recursive: true, watch: false);
        Assert.False(flat.Index.Loaded);
        flat.Start();
        deep.Start();
        await flat.Scanning;
        await deep.Scanning;
        Assert.Equal(["new.png", "old.png"], Names(flat));
        Assert.Equal(["new.png", "deep.png", "old.png"], Names(deep));
    }

    [Fact]
    public async Task Added_renamed_changed_and_deleted_files_are_followed()
    {
        using var dir = new TempFolder();
        TestImages.WritePng(dir.File("a.png"), lastWriteUtc: T0.AddHours(-1));
        using var cache = new FolderCache(dir.Path, recursive: false);
        cache.Start();
        await cache.Scanning;
        Assert.Equal(["a.png"], Names(cache));

        TestImages.WritePng(dir.File("b.png"));
        Assert.True(await TestImages.Eventually(() => Names(cache).SequenceEqual(["b.png", "a.png"])), string.Join(",", Names(cache)));

        File.Move(dir.File("b.png"), dir.File("c.gif.png"));
        Assert.True(await TestImages.Eventually(() => Names(cache).SequenceEqual(["c.gif.png", "a.png"])), string.Join(",", Names(cache)));

        File.SetLastWriteTimeUtc(dir.File("a.png"), DateTime.UtcNow.AddMinutes(5)); // edited: now the newest
        Assert.True(await TestImages.Eventually(() => Names(cache)[0] == "a.png"), string.Join(",", Names(cache)));

        File.Move(dir.File("a.png"), dir.File("a.txt")); // no longer an image
        File.Delete(dir.File("c.gif.png"));
        Assert.True(await TestImages.Eventually(() => Names(cache).Length == 0), string.Join(",", Names(cache)));
    }

    [Fact]
    public async Task Subfolder_files_come_and_go_with_their_folder()
    {
        using var dir = new TempFolder();
        using var cache = new FolderCache(dir.Path, recursive: true);
        cache.Start();
        await cache.Scanning;

        // A folder moved in reports only itself: its files arrive through a rescan.
        using var outside = new TempFolder();
        TestImages.WritePng(outside.File("x.png"));
        Directory.Move(outside.Path, dir.File("moved"));
        Assert.True(await TestImages.Eventually(() => Names(cache).SequenceEqual(["x.png"])), string.Join(",", Names(cache)));

        Directory.Delete(dir.File("moved"), recursive: true);
        Assert.True(await TestImages.Eventually(() => Names(cache).Length == 0), string.Join(",", Names(cache)));
    }

    [Fact]
    public async Task A_watcher_overflow_rescans_and_finds_what_events_missed()
    {
        using var dir = new TempFolder();
        TestImages.WritePng(dir.File("a.png"), lastWriteUtc: T0);
        using var cache = new FolderCache(dir.Path, recursive: false, watch: false); // no events at all
        cache.Start();
        await cache.Scanning;

        TestImages.WritePng(dir.File("missed.png"), lastWriteUtc: T0.AddMinutes(1));
        File.Delete(dir.File("a.png"));
        Assert.Equal(["a.png"], Names(cache)); // nothing enumerates the folder on its own

        var changed = 0;
        cache.Changed += () => Interlocked.Increment(ref changed);
        cache.OnOverflow();
        await cache.Scanning;
        Assert.Equal(["missed.png"], Names(cache));
        Assert.True(changed > 0);
    }

    [Theory]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00400000, true)] // RECALL_ON_DATA_ACCESS: OneDrive "online-only"
    [InlineData((FileAttributes)0x00040000, true)] // RECALL_ON_OPEN
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive, false)] // a hydrated OneDrive file
    [InlineData(FileAttributes.Normal, false)]
    public void Cloud_placeholders_are_told_apart_by_attributes_alone(FileAttributes attributes, bool cloudOnly) =>
        Assert.Equal(cloudOnly, FolderCache.IsCloudOnly(attributes));

    [Fact]
    public async Task Cloud_only_files_are_listed_but_never_chosen()
    {
        using var dir = new TempFolder();
        TestImages.WritePng(dir.File("local.png"), lastWriteUtc: T0.AddHours(-1));
        var cloud = TestImages.WritePng(dir.File("cloud.png"), lastWriteUtc: T0);
        File.SetAttributes(cloud, FileAttributes.Offline); // the one placeholder attribute a test can set
        using var cache = new FolderCache(dir.Path, recursive: false, watch: false);
        cache.Start();
        await cache.Scanning;

        Assert.True(cache.Index.Files[0].CloudOnly);
        Assert.Equal("local.png", FolderResolve.Newest(cache.Index.Files, ImageType.All)!.Name);
        Assert.Equal(["cloud", "local"], CollectionNaming.Name(cache.Index.Files, "", ImageType.All).Select(i => i.Name));
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_yet_is_picked_up_when_it_appears()
    {
        using var dir = new TempFolder();
        var later = dir.File("Screenshots");
        using var cache = new FolderCache(later, recursive: false);
        cache.Start();
        Assert.True(cache.Index.Loaded);
        Assert.Empty(cache.Index.Files);

        Directory.CreateDirectory(later);
        TestImages.WritePng(Path.Combine(later, "Screenshot 1.png"));
        Assert.True(await TestImages.Eventually(() => Names(cache).SequenceEqual(["Screenshot 1.png"])), string.Join(",", Names(cache)));
    }
}
