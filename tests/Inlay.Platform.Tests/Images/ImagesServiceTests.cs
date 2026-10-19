using Inlay.Core.Settings;
using Inlay.Platform.Images;
using Inlay.Platform.Input;

namespace Inlay.Platform.Tests.Images;

public sealed class ImagesServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<bool> ItemsSettle(ImagesService service, Func<IReadOnlyList<ImageItem>, bool> condition) =>
        await TestImages.Eventually(() => condition(service.Items));

    [Fact]
    public async Task Entries_become_items_with_unique_names_across_snippets_and_images()
    {
        using var lib = new TempFolder();
        using var gifs = new TempFolder();
        TestImages.WritePng(gifs.File("lgtm.png"));
        TestImages.WritePng(gifs.File("Ship It.png"));
        TestImages.WritePng(gifs.File("notes.png.txt"));
        var library = new ImageLibrary(lib.Path);
        var stored = library.Add(TestImages.WritePng(lib.File("src.png")))!;
        File.Delete(lib.File("src.png"));
        using var service = new ImagesService(library);

        ImageEntry[] entries =
        [
            new() { Name = "party", Kind = ImageEntryKind.Image, File = stored.File, Keywords = "yay" },
            new() { Name = "ship_it", Kind = ImageEntryKind.Folder, Folder = gifs.Path },
            new() { Kind = ImageEntryKind.Collection, Folder = gifs.Path, Prefix = "" },
        ];
        var changed = 0;
        service.ItemsChanged += () => Interlocked.Increment(ref changed);
        service.Update(true, entries, ["LGTM"]);

        Assert.True(await ItemsSettle(service, items => items.Count == 4), string.Join(",", service.Items.Select(i => i.Name)));
        Assert.Equal(["party", "ship_it", "lgtm_2", "ship_it_2"], service.Items.Select(i => i.Name));
        var party = service.Items[0];
        Assert.Equal(library.PathOf(stored.File), service.Resolve(party));
        Assert.Equal(stored.Size, party.File!.Size);
        Assert.Equal("yay", party.Keywords);
        Assert.True(changed > 0);
        Assert.Equal(["party", "ship_it"], service.Names(entries[2].Id));
    }

    [Fact]
    public async Task Collections_follow_their_folder()
    {
        using var lib = new TempFolder();
        using var gifs = new TempFolder();
        TestImages.WritePng(gifs.File("a.png"));
        using var service = new ImagesService(new ImageLibrary(lib.Path));
        service.Update(true, [new() { Kind = ImageEntryKind.Collection, Folder = gifs.Path, Prefix = "r-" }], []);
        Assert.True(await ItemsSettle(service, items => items.Select(i => i.Name).SequenceEqual(["r-a"])));

        TestImages.WritePng(gifs.File("Thumbs Up.png"));
        Assert.True(await ItemsSettle(service, items => items.Select(i => i.Name).SequenceEqual(["r-a", "r-thumbs_up"])), string.Join(",", service.Items.Select(i => i.Name)));
        File.Delete(gifs.File("a.png"));
        Assert.True(await ItemsSettle(service, items => items.Select(i => i.Name).SequenceEqual(["r-thumbs_up"])), string.Join(",", service.Items.Select(i => i.Name)));
    }

    [Fact]
    public async Task Folder_modes_resolve_from_the_cached_list()
    {
        using var lib = new TempFolder();
        using var shots = new TempFolder();
        for (var i = 0; i < 6; i++)
            TestImages.WritePng(shots.File($"shot{i}.png"), lastWriteUtc: T0.AddMinutes(i)); // shot5 is the newest
        TestImages.WritePng(shots.File("old.gif.png"), lastWriteUtc: T0.AddDays(-1));
        using var service = new ImagesService(new ImageLibrary(lib.Path));
        ImageEntry newest = new() { Name = "shot", Kind = ImageEntryKind.Folder, Folder = shots.Path, InsertAs = ImageInsertAs.File };
        ImageEntry random = new() { Name = "any", Kind = ImageEntryKind.Folder, Folder = shots.Path, Mode = FolderMode.Random };
        ImageEntry pick = new() { Name = "pick", Kind = ImageEntryKind.Folder, Folder = shots.Path, Mode = FolderMode.Pick, PickCount = 3 };
        service.Update(true, [newest, random, pick], []);
        Assert.True(await ItemsSettle(service, items => items.Count == 3 && items.All(service.IsLoaded)));

        var items = service.Items;
        Assert.Equal(shots.File("shot5.png"), service.Resolve(items[0]));
        Assert.Equal(shots.File("shot5.png"), service.Newest(items[0])!.Path);
        Assert.StartsWith(shots.Path, service.Resolve(items[1]), StringComparison.Ordinal);
        Assert.Equal(["shot5.png", "shot4.png", "shot3.png"], service.PickCandidates(items[2]).Select(f => f.Name));
        Assert.Equal(shots.File("shot4.png"), service.Resolve(items[2], pick: 1));
        Assert.Null(service.Resolve(items[2], pick: 3));

        var job = service.ToInsertion(items[0], "slack.exe", null)!;
        Assert.Equal((shots.File("shot5.png"), ImageInsertAs.File, ImagePasteMethod.Auto, "slack.exe"), (job.Path, job.InsertAs, job.Method, job.Process));
        Assert.Null(job.DisplayName); // folder files keep their own name
    }

    [Fact]
    public async Task Turning_the_feature_off_stops_everything()
    {
        using var lib = new TempFolder();
        using var shots = new TempFolder();
        TestImages.WritePng(shots.File("a.png"));
        using var service = new ImagesService(new ImageLibrary(lib.Path));
        ImageEntry[] entries = [new() { Name = "shot", Kind = ImageEntryKind.Folder, Folder = shots.Path }];
        service.Update(true, entries, []);
        Assert.True(await ItemsSettle(service, items => items.Count == 1));
        var cache = service.Items[0].Cache!;

        service.Update(false, entries, []);
        Assert.True(await ItemsSettle(service, items => items.Count == 0));
        TestImages.WritePng(shots.File("b.png"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Single(cache.Index.Files); // the watcher is gone

        service.Update(true, [], []); // on, but nothing to show: also nothing running
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Library_images_are_offered_under_their_entry_name()
    {
        using var lib = new TempFolder();
        var library = new ImageLibrary(lib.Path);
        var stored = library.Add(TestImages.WritePng(lib.File("x.png")))!;
        using var service = new ImagesService(library);
        service.Update(true, [new() { Name = "lgtm", Kind = ImageEntryKind.Image, File = stored.File }], []);
        Assert.True(await ItemsSettle(service, items => items.Count == 1));
        var job = service.ToInsertion(service.Items[0], "outlook.exe", new Dictionary<string, string>())!;
        Assert.Equal("lgtm.png", job.DisplayName);
        Assert.Equal(ImagePasteMethod.Bitmap, job.Method);
    }
}
