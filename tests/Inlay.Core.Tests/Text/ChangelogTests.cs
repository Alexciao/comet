using Inlay.Core.Text;

namespace Inlay.Core.Tests.Text;

public sealed class ChangelogTests
{
    private const string Sample = """
        # Changelog

        All notable changes to this project are documented here.

        ## [Unreleased]

        ### Added
        - Something in progress

        ## [1.1.0] - 2026-10-20

        ### Added
        - Shorthands expand as you type.

        ### Fixed
        - The tray icon is in color.

        ## [1.0.0] - 2026-09-01

        - First release.

        [Unreleased]: https://github.com/x/y/compare/v1.1.0...HEAD
        [1.1.0]: https://github.com/x/y/compare/v1.0.0...v1.1.0
        [1.0.0]: https://github.com/x/y/releases/tag/v1.0.0
        """;

    [Fact]
    public void A_version_section_is_its_body_without_the_heading()
    {
        Assert.Equal("### Added\n- Shorthands expand as you type.\n\n### Fixed\n- The tray icon is in color.", Changelog.Section(Sample, "1.1.0"));
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("1.1")]
    [InlineData("1.1.0.0")]
    public void Versions_match_however_they_are_written(string version) =>
        Assert.StartsWith("### Added\n- Shorthands", Changelog.Section(Sample, version), StringComparison.Ordinal);

    [Fact]
    public void Unreleased_is_a_section_too() =>
        Assert.Equal("### Added\n- Something in progress", Changelog.Section(Sample, "Unreleased"));

    [Fact]
    public void The_last_section_stops_before_the_link_references() =>
        Assert.Equal("- First release.", Changelog.Section(Sample, "1.0.0"));

    [Fact]
    public void Crlf_line_endings_are_read() =>
        Assert.Equal("- First release.", Changelog.Section(Sample.ReplaceLineEndings("\r\n"), "1.0.0"));

    [Fact]
    public void A_missing_version_is_null()
    {
        Assert.Null(Changelog.Section(Sample, "0.0.0"));
        Assert.Null(Changelog.Section("", "1.1.0"));
    }

    [Fact]
    public void Headings_without_brackets_and_an_empty_section_work()
    {
        Assert.Equal("- Patch.", Changelog.Section("## 1.0.1 (2026-09-10)\n- Patch.\n## 1.0.0\n", "1.0.1"));
        Assert.Equal("", Changelog.Section("## [1.2.0] - 2026-11-01\n\n## [1.1.0]\n- x\n", "1.2.0"));
    }
}
