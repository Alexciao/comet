using System.Text.RegularExpressions;
using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>
/// Keystroke → popup latency as measured inside Comet (hook callback to the rendered frame), over many sessions.
/// Comet logs the percentiles when it quits; this test asserts the p95 target and prints the numbers for PERF.md.
/// </summary>
[Collection(Desktop.Name)]
public sealed partial class PerformanceTests : IDisposable
{
    private readonly CometApp _comet = new();
    private readonly NotepadTarget _notepad;

    public PerformanceTests()
    {
        try
        {
            _comet.Start();
            _notepad = new NotepadTarget();
        }
        catch
        {
            _comet.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _notepad.Dispose();
        _comet.Dispose();
    }

    [Fact]
    public void Keystroke_to_popup_is_under_one_frame_at_p95()
    {
        // Comet normally starts at login, long before the first ":"; let its emoji warm-up finish first.
        Wait.Until(() => _comet.Log.Contains("emoji bitmaps ready", StringComparison.Ordinal), TimeSpan.FromSeconds(60), "emoji warm-up");
        _notepad.Focus();
        string[] queries = Environment.GetEnvironmentVariable("COMET_PERF_SAME") == "1"
            ? ["smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi"]
            : ["smi", "hea", "thu", "fir", "roc", "tad", "joy", "cof", "piz", "dog"];
        for (var round = 0; round < 3; round++)
        {
            foreach (var q in queries)
            {
                Keyboard.Type(" :" + q);
                _comet.WaitForPopup();
                Keyboard.Press(Keyboard.VK_ESCAPE);
                _comet.WaitForPopupClosed();
            }
        }

        _comet.Stop();
        var lines = _comet.Log.Split('\n');
        foreach (var l in lines.Where(l => l.Contains("keystroke to popup", StringComparison.Ordinal) || l.Contains("] hook ", StringComparison.Ordinal)))
            TestContext.Current.TestOutputHelper?.WriteLine(l.Trim());

        // The target is for the warm app: emoji the popup has drawn before (or pre-rendered at startup).
        var line = lines.Last(l => l.Contains("keystroke to popup rendered:", StringComparison.Ordinal));
        var p95 = double.Parse(P95().Match(line).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // The target is for the shipped (Release, Native AOT) build: run with COMET_EXE pointing at the publish
        // output. A Debug build is measured and reported, but its numbers aren't held to the target.
        if (!CometApp.IsReleaseBuild)
            Assert.Skip($"Debug build measured, not asserted ({line.Trim()}); set COMET_EXE to the published Comet.exe.");
        Assert.True(p95 < 16_000, line);
    }

    [GeneratedRegex(@"p95=([0-9.]+)")]
    private static partial Regex P95();
}
