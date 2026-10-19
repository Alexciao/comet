using Inlay.Core.Emoji;
using Inlay.Platform.Fonts;

namespace Inlay.Platform.Tests;

/// <summary>The runtime font check against the installed Segoe UI Emoji.</summary>
public sealed class EmojiFontCheckTests
{
    [Fact]
    public void Everyday_emoji_are_drawable_and_flags_and_unknown_sequences_are_not()
    {
        // 🇮🇹: Segoe UI Emoji has the regional indicator letters but no flag ligatures (two letters side by side).
        // 🔥🔥: two emoji are twice as wide as one.
        string[] sequences = ["😀", "🔥", "👍", "👍🏽", "❤️", "👨‍👩‍👧", "🇮🇹", "🔥🔥", "\U0010FFFD"];
        var result = EmojiFontCheck.Check(sequences);
        Assert.NotNull(result);
        Assert.Equal([true, true, true, true, true, true, false, false, false], result);
    }

    [Fact]
    public void The_check_mostly_agrees_with_the_data_builder_on_this_machine()
    {
        // The embedded flags were computed against the developer machine's Segoe UI Emoji; CI runners can have
        // another build of the font, where differences are expected and are exactly what the runtime check is for.
        if (Environment.GetEnvironmentVariable("CI") == "true")
            Assert.Skip("Compares against the font the data was built with; the runner's font may differ.");
        using var data = EmojiData.LoadEmbedded();
        var sequences = Enumerable.Range(0, data.Count).Select(i => data.Emoji(i).ToString()).ToArray();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = EmojiFontCheck.Check(sequences)!;
        var elapsed = started.Elapsed;
        var disagree = Enumerable.Range(0, data.Count).Where(i => result[i] != data.RenderedOnReferenceFont(i)).Select(i => sequences[i]).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"{data.Count} emoji checked in {elapsed.TotalMilliseconds:F0} ms; " +
            $"{result.Count(r => !r)} not drawable; {disagree.Length} differ from the build-time flag: {string.Join(' ', disagree)}");
        // The builder measured the same font with HarfBuzz; allow a handful of borderline widths.
        Assert.True(disagree.Length <= 10, string.Join(' ', disagree));
    }
}
