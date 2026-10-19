using System.Globalization;
using Inlay.Core.Images;
using Inlay.Core.Settings;

namespace Inlay.Core.Tests.Images;

public sealed class ImageTextTests
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private static readonly DateTime Now = new(2026, 10, 19, 15, 30, 0);

    [Theory]
    [InlineData(ImageType.Gif, 1_258_291, "GIF · 1.2 MB")]
    [InlineData(ImageType.Png, 348_160, "PNG · 340 KB")]
    [InlineData(ImageType.Jpeg, 512, "JPEG · 512 bytes")]
    [InlineData(ImageType.WebP, 31_457_280, "WebP · 30 MB")]
    [InlineData(ImageType.Bmp, -1, "BMP")]
    public void Describes_type_and_size(ImageType type, long size, string expected) =>
        Assert.Equal(expected, ImageText.Describe(type, size, En));

    [Theory]
    [InlineData(0, 0, 20, "just now")]
    [InlineData(0, 2, 0, "2 min ago")]
    [InlineData(0, 59, 0, "59 min ago")]
    [InlineData(3, 0, 0, "3 h ago")]
    [InlineData(15, 0, 0, "15 h ago")]   // still today
    [InlineData(17, 0, 0, "yesterday")]  // 22:30 yesterday
    [InlineData(40, 0, 0, "17 Oct")]
    [InlineData(24 * 300, 0, 0, "23 Dec 2025")]
    public void Relative_times(int hours, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, ImageText.Relative(Now - new TimeSpan(hours, minutes, seconds), Now, En));

    [Fact]
    public void A_late_evening_shot_seen_after_midnight_is_hours_ago_not_yesterday()
    {
        var now = new DateTime(2026, 10, 19, 1, 0, 0);
        Assert.Equal("2 h ago", ImageText.Relative(now.AddHours(-2), now, En));
    }

    [Fact]
    public void Folder_rows()
    {
        Assert.Equal("Latest in Screenshots", ImageText.Folder(FolderMode.Newest, ImageText.FolderName("{Screenshots}")));
        Assert.Equal("Random in Memes", ImageText.Folder(FolderMode.Random, ImageText.FolderName(@"D:\Pictures\Memes\")));
        Assert.Equal("Pick from Camera Roll", ImageText.Folder(FolderMode.Pick, ImageText.FolderName("{cameraroll}")));
        Assert.Equal("Collection · Reactions · 24 images", ImageText.Collection("Reactions", 24, En));
    }
}
