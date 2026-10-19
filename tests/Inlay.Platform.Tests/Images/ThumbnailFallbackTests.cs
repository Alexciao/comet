using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

public sealed class ThumbnailFallbackTests
{
    [Fact]
    public void An_empty_shell_result_decodes_a_cloud_placeholder_does_not_and_a_missing_file_gets_the_glyph()
    {
        Assert.Equal(ThumbnailKind.Decode, ThumbnailFallback.Decide(shellProduced: false, exists: true, cloudOnly: false, length: 1024));
        Assert.Equal(ThumbnailKind.Placeholder, ThumbnailFallback.Decide(shellProduced: false, exists: true, cloudOnly: true, length: 1024));
        Assert.Equal(ThumbnailKind.Glyph, ThumbnailFallback.Decide(shellProduced: false, exists: false, cloudOnly: false, length: 0));
        Assert.Equal(ThumbnailKind.Glyph, ThumbnailFallback.Decide(shellProduced: false, exists: true, cloudOnly: false, length: ThumbnailFallback.MaxDecodeBytes + 1));
        Assert.Equal(ThumbnailKind.Shell, ThumbnailFallback.Decide(shellProduced: true, exists: true, cloudOnly: false, length: 1024));
    }
}
