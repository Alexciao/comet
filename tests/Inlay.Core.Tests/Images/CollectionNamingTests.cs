using Inlay.Core.Images;

namespace Inlay.Core.Tests.Images;

public sealed class CollectionNamingTests
{
    private static ImageFile F(string path, int minutesAgo = 0) => new(path, new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo), 100);

    [Theory]
    [InlineData("thumbs-up.gif", "thumbs_up")]
    [InlineData("Thumbs Up!.png", "thumbs_up")]
    [InlineData("  --Party__Parrot--  .webp", "party_parrot")]
    [InlineData("café.gif", "cafe")]
    [InlineData("don't panic.jpg", "dont_panic")]
    [InlineData("IMG_2041.JPEG", "img_2041")]
    [InlineData("100.png", "100")]
    [InlineData("a+b.png", "a_b")]
    public void File_names_become_snake_case(string file, string expected) =>
        Assert.Equal(expected, CollectionNaming.FromFileName(file));

    [Theory]
    [InlineData("日本.png")]
    [InlineData("!!!.gif")]
    [InlineData(".png")]
    public void Names_that_end_up_empty_are_skipped(string file)
    {
        Assert.Null(CollectionNaming.FromFileName(file));
        Assert.Empty(CollectionNaming.Name([F($@"C:\x\{file}")], "", ImageType.All));
    }

    [Fact]
    public void The_prefix_is_used_as_typed()
    {
        var items = CollectionNaming.Name([F(@"C:\r\thumbs-up.gif")], "gif-", ImageType.All);
        Assert.Equal("gif-thumbs_up", Assert.Single(items).Name);
        Assert.Equal("GIF-thumbs_up", CollectionNaming.Name([F(@"C:\r\thumbs-up.gif")], "GIF-", ImageType.All)[0].Name);
    }

    [Fact]
    public void Duplicates_are_numbered_the_same_way_whatever_the_order()
    {
        ImageFile[] files = [F(@"C:\r\Thumbs Up.png", 1), F(@"C:\r\thumbs-up.gif", 2), F(@"C:\r\sub\thumbs_up.webp", 3), F(@"C:\r\cat.png")];
        var a = CollectionNaming.Name(files, "", ImageType.All);
        var b = CollectionNaming.Name([.. files.Reverse()], "", ImageType.All);
        Assert.Equal(a, b);
        Assert.Equal(["cat", "thumbs_up", "thumbs_up_2", "thumbs_up_3"], a.Select(i => i.Name));
        Assert.Equal(4, a.Select(i => i.File.Path).Distinct().Count());
    }

    [Fact]
    public void Names_taken_by_snippets_or_other_images_are_numbered_case_insensitively()
    {
        var items = CollectionNaming.Name([F(@"C:\r\lgtm.gif")], "", ImageType.All, n => string.Equals(n, "LGTM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("lgtm_2", Assert.Single(items).Name);
    }

    [Fact]
    public void Only_the_chosen_file_types_become_items()
    {
        var items = CollectionNaming.Name([F(@"C:\r\a.gif"), F(@"C:\r\b.png"), F(@"C:\r\c.txt")], "", ImageTypes.Filter(["gif"]));
        Assert.Equal("a", Assert.Single(items).Name);
    }
}
