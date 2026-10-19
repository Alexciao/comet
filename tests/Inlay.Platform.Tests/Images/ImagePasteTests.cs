using Inlay.Core.Settings;
using Inlay.Platform.Images;
using Inlay.Platform.Input;

namespace Inlay.Platform.Tests.Images;

/// <summary>What an image paste puts on the clipboard, built without touching the user's clipboard.</summary>
public sealed class ImagePasteTests
{
    private static ImageInsertion Job(string path, ImageInsertAs insertAs = ImageInsertAs.Image, string method = ImagePasteMethod.Auto) =>
        new(path, insertAs, method, "app.exe");

    [Fact]
    public void Insert_as_image_offers_the_bitmap_first_and_the_file_too()
    {
        Assert.Equal([ImageFormat.Png, ImageFormat.DibV5, ImageFormat.File], ImagePasteMethod.Formats(Job(@"C:\a.png")));
        Assert.Equal([ImageFormat.Png, ImageFormat.DibV5, ImageFormat.File], ImagePasteMethod.Formats(Job(@"C:\a.jpg")));
    }

    [Fact]
    public void Gifs_go_in_as_a_file_first_so_they_stay_animated() =>
        Assert.Equal([ImageFormat.File, ImageFormat.Png, ImageFormat.DibV5], ImagePasteMethod.Formats(Job(@"C:\lgtm.GIF")));

    [Fact]
    public void Insert_as_file_is_the_file_only_and_a_path_is_typed()
    {
        Assert.Equal([ImageFormat.File], ImagePasteMethod.Formats(Job(@"C:\a.png", ImageInsertAs.File)));
        Assert.Equal([ImageFormat.File], ImagePasteMethod.Formats(Job(@"C:\a.gif", ImageInsertAs.File, ImagePasteMethod.Bitmap)));
        Assert.Empty(ImagePasteMethod.Formats(Job(@"C:\a.png", ImageInsertAs.Path)));
    }

    [Fact]
    public void Learned_methods_choose_the_formats()
    {
        Assert.Equal([ImageFormat.Png, ImageFormat.DibV5], ImagePasteMethod.Formats(Job(@"C:\a.gif", method: ImagePasteMethod.Bitmap)));
        Assert.Equal([ImageFormat.File], ImagePasteMethod.Formats(Job(@"C:\a.png", method: ImagePasteMethod.File)));
    }

    [Fact]
    public void Methods_per_app_learned_then_built_in_then_auto()
    {
        var learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Slack.exe"] = "bitmap", ["odd.exe"] = "nonsense" };
        Assert.Equal(ImagePasteMethod.Bitmap, ImagePasteMethod.For("slack.exe", learned));
        Assert.Equal(ImagePasteMethod.Bitmap, ImagePasteMethod.For("OUTLOOK.EXE", learned));
        Assert.Equal(ImagePasteMethod.File, ImagePasteMethod.For("explorer.exe", null));
        Assert.Equal(ImagePasteMethod.Auto, ImagePasteMethod.For("odd.exe", learned));
        Assert.Equal(ImagePasteMethod.Auto, ImagePasteMethod.For("discord.exe", learned));
        Assert.Equal(
            [ImagePasteMethod.Bitmap, ImagePasteMethod.File, ImagePasteMethod.Auto],
            new[] { ImagePasteMethod.Auto, ImagePasteMethod.Bitmap, ImagePasteMethod.File }.Select(ImagePasteMethod.AfterUnread));
    }

    [Fact]
    public void Png_files_are_handed_over_as_they_are_and_other_types_are_converted()
    {
        using var dir = new TempFolder();
        var png = TestImages.WritePng(dir.File("a.png"), 20, 10);
        var data = new ImagePasteData(png);
        Assert.Equal(File.ReadAllBytes(png), data.Render(ImageFormat.Png));
        var dib = Dib.Decode(data.Render(ImageFormat.DibV5)!)!;
        Assert.Equal((20, 10), (dib.Width, dib.Height));
        Assert.Equal([png], DropFiles.Decode(data.Render(ImageFormat.File)!));

        var bmp = TestImages.WriteBmp(dir.File("b.bmp"), TestImages.Gradient(7, 5, alpha: 255));
        var converted = new ImagePasteData(bmp).Render(ImageFormat.Png)!;
        Assert.True(Png.IsPng(converted));
        File.WriteAllBytes(dir.File("converted.png"), converted);
        Assert.Equal(TestImages.Gradient(7, 5).Pixels, ImageCodec.Decode(dir.File("converted.png"))!.Pixels);
    }

    [Fact]
    public void An_unreadable_file_renders_nothing()
    {
        var data = new ImagePasteData(@"C:\definitely\not\here.jpg");
        Assert.Null(data.Render(ImageFormat.Png));
        Assert.Null(data.Render(ImageFormat.DibV5));
        Assert.NotNull(data.Render(ImageFormat.File)); // the file list needs no reading
    }

    [Fact]
    public void Library_files_are_offered_under_their_entry_name()
    {
        using var dir = new TempFolder();
        var hashed = TestImages.WritePng(dir.File(new string('a', 64) + ".png"));
        var staged = ImagePasteData.Stage(hashed, "lgtm.png");
        try
        {
            Assert.Equal("lgtm.png", Path.GetFileName(staged));
            Assert.Equal(File.ReadAllBytes(hashed), File.ReadAllBytes(staged));
            Assert.Equal([staged], DropFiles.Decode(new ImagePasteData(hashed, staged).Render(ImageFormat.File)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(staged)!, recursive: true);
        }

        Assert.Equal(hashed, ImagePasteData.Stage(hashed, null));
        Assert.Equal(hashed, ImagePasteData.Stage(hashed, "bad:name.png"));
    }
}
