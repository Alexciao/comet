using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

public sealed class ThumbnailsTests
{
    [Fact]
    public async Task A_generated_png_gets_a_thumbnail_that_fits_the_box()
    {
        using var dir = new TempFolder();
        var path = TestImages.WritePng(dir.File("wide.png"), w: 200, h: 100);
        using var thumbnails = new Thumbnails();
        Assert.Null(thumbnails.TryGetCached(path, 64));

        var t = await thumbnails.GetAsync(path, 64);
        Assert.NotNull(t);
        Assert.True(t.Width <= 64 && t.Height <= 64 && t.Width > 0 && t.Height > 0, $"{t.Width}×{t.Height}");
        Assert.Equal(t.Width * t.Height * 4, t.Bgra.Length);
        Assert.Same(t, thumbnails.TryGetCached(path, 64));
    }

    [Fact]
    public async Task Missing_files_get_none_and_the_cache_stays_bounded()
    {
        using var dir = new TempFolder();
        using var thumbnails = new Thumbnails(capacity: 2);
        Assert.Null(await thumbnails.GetAsync(dir.File("missing.png"), 48));

        var paths = Enumerable.Range(0, 3).Select(i => TestImages.WritePng(dir.File($"{i}.png"), seed: (byte)(i * 40))).ToArray();
        foreach (var p in paths)
            Assert.NotNull(await thumbnails.GetAsync(p, 48));
        Assert.Equal(2, thumbnails.Count);
        Assert.Null(thumbnails.TryGetCached(paths[0], 48)); // the least recently used went
        Assert.NotNull(thumbnails.TryGetCached(paths[2], 48));
    }

    [Fact]
    public async Task A_changed_file_is_a_different_thumbnail()
    {
        using var dir = new TempFolder();
        var path = TestImages.WritePng(dir.File("a.png"));
        using var thumbnails = new Thumbnails();
        var v1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(await thumbnails.GetAsync(path, 32, v1));
        Assert.Null(thumbnails.TryGetCached(path, 32, v1.AddSeconds(1)));
    }
}
