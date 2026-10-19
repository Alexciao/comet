using System.Text;
using Inlay.Core.Settings;
using Inlay.Core.Updates;

namespace Inlay.Core.Tests.Updates;

public sealed class AppReleaseTests
{
    private static AppRelease? Parse(string json) => AppRelease.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_published_release_is_read()
    {
        var r = Parse("""{ "tag_name": "v1.3.0", "html_url": "https://github.com/Alexciao/inlay/releases/tag/v1.3.0", "draft": false, "prerelease": false }""");
        Assert.Equal(new Version(1, 3, 0), r!.Version);
        Assert.Equal("1.3.0", r.DisplayVersion);
        Assert.True(r.IsNewerThan(new Version(1, 2, 9)));
        Assert.False(r.IsNewerThan(new Version(1, 3, 0)));
        Assert.False(r.IsNewerThan(new Version(1, 3, 0, 0)));
    }

    [Fact]
    public void The_tag_notes_and_date_come_along()
    {
        var r = Parse("""{ "tag_name": "v1.3.0", "html_url": "https://github.com/x/y", "body": "## What's new\r\n- More", "published_at": "2026-10-20T08:30:00Z" }""");
        Assert.Equal("v1.3.0", r!.Tag);
        Assert.Equal("## What's new\r\n- More", r.Notes);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), r.Published);

        var bare = Parse("""{ "tag_name": "v1.3.0", "html_url": "https://github.com/x/y", "body": "", "published_at": null }""");
        Assert.Null(bare!.Notes);
        Assert.Null(bare.Published);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v2.0.0", "html_url": "https://github.com/x/y", "prerelease": true }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "html_url": "https://github.com/x/y", "draft": true }""")]
    [InlineData("""{ "tag_name": "emoji-data", "html_url": "https://github.com/x/y" }""")]
    [InlineData("""{ "tag_name": "v2.0.0-beta", "html_url": "https://github.com/x/y" }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "html_url": "http://github.com/x/y" }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "html_url": "https://evil.example/x" }""")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""not json""")]
    public void Anything_else_is_ignored(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V10.0.1", "10.0.1")]
    public void Tags_parse_to_three_part_versions(string tag, string display)
    {
        Assert.True(AppRelease.TryParseTag(tag, out _, out var shown));
        Assert.Equal(display, shown);
    }

    [Fact]
    public void Notifications_follow_the_setting_and_pause()
    {
        var s = new AppSettings();
        Assert.True(s.NotifyAppUpdates); // on by default
        Assert.True(AppUpdatePolicy.Enabled(s, paused: false));
        Assert.False(AppUpdatePolicy.Enabled(s, paused: true));
        s.NotifyAppUpdates = false;
        Assert.False(AppUpdatePolicy.Enabled(s, paused: false));
    }
}
