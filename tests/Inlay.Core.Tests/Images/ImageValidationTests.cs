using Inlay.Core.Settings;

namespace Inlay.Core.Tests.Images;

/// <summary>Names are unique across snippets and images; emoji collisions only warn; users name things as they like.</summary>
public sealed class ImageValidationTests
{
    private static readonly Func<string, bool> Emoji = n => n is "fire" or "smile";

    private static ImageEntry Image(string name, string? file = "ab.gif") => new() { Name = name, Kind = ImageEntryKind.Image, File = file };

    private static IReadOnlyList<ValidationMessage> Validate(ImageEntry e, string[]? snippets = null, string[]? images = null, long? size = null) =>
        EntryValidation.ValidateImage(e, snippets ?? [], images ?? [], Emoji, size);

    [Fact]
    public void An_image_name_used_by_a_snippet_or_another_image_is_refused_in_any_case()
    {
        var m = Assert.Single(Validate(Image("LGTM"), snippets: ["lgtm"]));
        Assert.Equal((Severity.Error, ValidationField.Name), (m.Severity, m.Field));
        Assert.Contains("snippet", m.Text, StringComparison.Ordinal);
        m = Assert.Single(Validate(Image("lgtm"), images: ["Lgtm"]));
        Assert.Contains("image named", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snippet_name_used_by_an_image_is_refused()
    {
        var m = EntryValidation.ValidateSnippet("Screenshot", "x", [], ["screenshot"], Emoji);
        Assert.True(EntryValidation.HasErrors(m));
        Assert.Contains("image named", m[0].Text, StringComparison.Ordinal);
        Assert.Empty(EntryValidation.ValidateSnippet("sig", "x", ["other"], ["screenshot"], Emoji));
    }

    [Fact]
    public void An_emoji_shortcode_only_warns_and_the_image_wins()
    {
        var m = Assert.Single(EntryValidation.ValidateImage(Image("fire"), [], [], Emoji, trigger: ";"));
        Assert.Equal(Severity.Warning, m.Severity);
        Assert.Contains("“;fire”", m.Text, StringComparison.Ordinal);
        Assert.Contains("Your image is inserted instead", m.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my gif")]
    [InlineData("lg.tm")]
    public void Invalid_names_are_errors(string name) =>
        Assert.True(EntryValidation.HasErrors(Validate(Image(name))));

    [Theory]
    [InlineData("LGTM")]
    [InlineData("Ship-It")]
    [InlineData("+1")]
    [InlineData("thumbs_up")]
    public void Users_name_images_as_they_like_with_no_warning(string name) =>
        Assert.Empty(Validate(Image(name)));

    [Fact]
    public void Large_files_warn_about_chat_upload_limits()
    {
        Assert.Empty(Validate(Image("ok"), size: EntryValidation.LargeImageBytes));
        var m = Assert.Single(Validate(Image("big"), size: 31L * 1024 * 1024));
        Assert.Equal((Severity.Warning, ValidationField.File), (m.Severity, m.Field));
        Assert.Contains("31 MB", m.Text, StringComparison.Ordinal);
        Assert.Contains("Many chat apps reject files over 25 MB", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_needs_a_file_and_a_folder_or_collection_a_folder()
    {
        Assert.Equal(ValidationField.File, Assert.Single(Validate(Image("x", file: null))).Field);
        Assert.Equal(ValidationField.Folder, Assert.Single(Validate(new ImageEntry { Name = "shot", Kind = ImageEntryKind.Folder })).Field);
        Assert.Empty(Validate(new ImageEntry { Name = "screenshot", Kind = ImageEntryKind.Folder, Folder = "{Screenshots}" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gif-")]
    [InlineData("GIF_")]
    [InlineData("React+")]
    public void Collection_prefixes_are_free_form_and_may_be_empty(string prefix) =>
        Assert.Empty(Validate(new ImageEntry { Kind = ImageEntryKind.Collection, Folder = @"C:\gifs", Prefix = prefix }, snippets: ["gif-"]));

    [Fact]
    public void A_collection_prefix_with_spaces_is_an_error()
    {
        var m = Assert.Single(Validate(new ImageEntry { Kind = ImageEntryKind.Collection, Folder = @"C:\gifs", Prefix = "my gifs" }));
        Assert.Equal(ValidationField.Prefix, m.Field);
    }
}
