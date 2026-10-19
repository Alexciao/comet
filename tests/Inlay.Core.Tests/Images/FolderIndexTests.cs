using Inlay.Core.Images;

namespace Inlay.Core.Tests.Images;

public sealed class FolderIndexTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ImageFile F(string name, int minutesAgo, bool cloud = false) => new($@"C:\shots\{name}", T0.AddMinutes(-minutesAgo), 1000, cloud);

    private static FolderIndex Index(params ImageFile[] files)
    {
        var index = new FolderIndex();
        index.Reset(files);
        return index;
    }

    [Fact]
    public void A_scan_is_sorted_newest_first_and_drops_other_files()
    {
        var index = Index(F("old.png", 30), F("new.png", 1), F("notes.txt", 0), F("mid.gif", 10));
        Assert.True(index.Loaded);
        Assert.Equal(["new.png", "mid.gif", "old.png"], index.Files.Select(f => f.Name));
    }

    [Fact]
    public void Added_changed_renamed_and_deleted_files_keep_the_order()
    {
        var index = Index(F("a.png", 30), F("b.png", 20));
        index.Upsert(F("c.png", 0));                  // a new screenshot
        Assert.Equal("c.png", index.Files[0].Name);
        index.Upsert(F("a.png", -1));                 // edited: now the newest
        Assert.Equal(["a.png", "c.png", "b.png"], index.Files.Select(f => f.Name));
        index.Rename(@"C:\shots\b.png", F("b2.png", 20));
        Assert.Contains(index.Files, f => f.Name == "b2.png");
        Assert.DoesNotContain(index.Files, f => f.Name == "b.png");
        index.Rename(@"C:\shots\b2.png", null);       // renamed to something that isn't an image
        Assert.True(index.Remove(@"C:\SHOTS\A.PNG"));  // paths are case-insensitive
        Assert.False(index.Remove(@"C:\shots\missing.png"));
        Assert.Equal(["c.png"], index.Files.Select(f => f.Name));
    }

    [Fact]
    public void Removing_a_subfolder_removes_its_files_only()
    {
        var index = Index(new ImageFile(@"C:\p\sub\a.png", T0, 1), new ImageFile(@"C:\p\subway.png", T0, 1), new ImageFile(@"C:\p\b.png", T0, 1));
        Assert.Equal(1, index.RemoveUnder(@"C:\p\sub"));
        Assert.Equal(2, index.Files.Count);
    }

    [Fact]
    public void A_snapshot_never_changes_after_it_was_read()
    {
        var index = Index(F("a.png", 5));
        var before = index.Files;
        index.Upsert(F("b.png", 0));
        index.Remove(@"C:\shots\a.png");
        Assert.Equal(["a.png"], before.Select(f => f.Name));
    }

    [Fact]
    public void Changes_are_announced()
    {
        var index = new FolderIndex();
        var count = 0;
        index.Changed += () => count++;
        index.Reset([F("a.png", 1)]);
        index.Upsert(F("b.png", 0));
        index.Upsert(F("b.png", 0)); // same: nothing changed
        index.Remove(@"C:\shots\a.png");
        Assert.Equal(3, count);
    }

    [Fact]
    public void Newest_and_pick_honour_the_file_types_and_skip_cloud_only_files()
    {
        var files = Index(F("cloud.png", 0, cloud: true), F("anim.gif", 1), F("shot.png", 2), F("older.png", 3), F("oldest.jpg", 4)).Files;
        Assert.Equal("anim.gif", FolderResolve.Newest(files, ImageType.All)!.Name);
        Assert.Equal("shot.png", FolderResolve.Newest(files, ImageTypes.Filter(["png"]))!.Name);
        Assert.Equal("oldest.jpg", FolderResolve.Newest(files, ImageTypes.Filter(["jpeg"]))!.Name);
        Assert.Null(FolderResolve.Newest(files, ImageType.WebP));
        Assert.Equal(["anim.gif", "shot.png", "older.png"], FolderResolve.Newest(files, ImageType.All, 3).Select(f => f.Name));
        Assert.Equal(["shot.png", "older.png"], FolderResolve.Newest(files, ImageType.Png, 8).Select(f => f.Name));
    }

    [Fact]
    public void Random_reaches_every_usable_file_and_nothing_else()
    {
        var files = Index(F("cloud.png", 0, cloud: true), F("a.png", 1), F("b.png", 2), F("c.gif", 3)).Files;
        var rng = new Random(42);
        var seen = Enumerable.Range(0, 200).Select(_ => FolderResolve.Random(files, ImageType.Png, rng)!.Name).ToHashSet();
        Assert.Equal(["a.png", "b.png"], seen.Order());
        Assert.Null(FolderResolve.Random(files, ImageType.Bmp, rng));
        Assert.Null(FolderResolve.Random([], ImageType.All, rng));
    }

    [Fact]
    public async Task Readers_never_see_a_torn_list_while_files_change()
    {
        var index = Index([.. Enumerable.Range(0, 200).Select(i => F($"f{i}.png", i))]);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                index.Upsert(F($"n{i % 50}.png", -i));
                index.Remove($@"C:\shots\n{(i + 25) % 50}.png");
            }
        }, TestContext.Current.CancellationToken);
        for (var r = 0; r < 2000; r++)
        {
            var files = index.Files;
            for (var i = 1; i < files.Count; i++)
                Assert.True(files[i - 1].LastWriteUtc >= files[i].LastWriteUtc);
        }

        await stop.CancelAsync();
        await writer;
    }
}
