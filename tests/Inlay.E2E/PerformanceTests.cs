using System.Text.RegularExpressions;
using Inlay.E2E.Harness;

namespace Inlay.E2E;

/// <summary>
/// Keystroke → popup latency as measured inside Inlay (hook callback to the rendered frame), over many sessions.
/// Inlay logs the percentiles when it quits; this test asserts the p95 target and prints the numbers for PERF.md.
/// </summary>
[Collection(Desktop.Name)]
public sealed partial class PerformanceTests : IDisposable
{
    private readonly InlayApp _inlay = new();
    private readonly NotepadTarget _notepad;

    public PerformanceTests()
    {
        try
        {
            _inlay.Start();
            _notepad = new NotepadTarget();
        }
        catch
        {
            _inlay.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _notepad.Dispose();
        _inlay.Dispose();
    }

    [Fact]
    public void Keystroke_to_popup_is_under_one_frame_at_p95() => Measure(afterSettings: false);

    /// <summary>
    /// Closing Settings releases its window and trims Inlay's working set; the popup must still be as fast the next
    /// time (its pages come back from the standby list).
    /// </summary>
    [Fact]
    public void Keystroke_to_popup_stays_under_one_frame_after_Settings_was_closed() => Measure(afterSettings: true);

    private void Measure(bool afterSettings)
    {
        // Inlay normally starts at login, long before the first ":"; let its emoji warm-up finish first.
        Wait.Until(() => _inlay.Log.Contains("emoji bitmaps ready", StringComparison.Ordinal), TimeSpan.FromSeconds(60), "emoji warm-up");
        if (afterSettings)
        {
            _inlay.OpenSettings();
            var window = Automation.WaitForWindow(w => w.Name == "Inlay Settings", TimeSpan.FromSeconds(10), "Settings");
            window.Patterns.Window.Pattern.Close();
            Wait.Until(() => _inlay.Log.Contains("settings released", StringComparison.Ordinal), TimeSpan.FromSeconds(15), "Settings to be released");
        }

        _notepad.Focus();
        string[] queries = Environment.GetEnvironmentVariable("INLAY_PERF_SAME") == "1"
            ? ["smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi", "smi"]
            : ["smi", "hea", "thu", "fir", "roc", "tad", "joy", "cof", "piz", "dog"];
        for (var round = 0; round < 3; round++)
        {
            foreach (var q in queries)
            {
                Keyboard.Type(" :" + q);
                _inlay.WaitForPopup();
                Keyboard.Press(Keyboard.VK_ESCAPE);
                _inlay.WaitForPopupClosed();
            }
        }

        _inlay.Stop();
        var lines = _inlay.Log.Split('\n');
        foreach (var l in lines.Where(l => l.Contains("keystroke to popup", StringComparison.Ordinal) || l.Contains("] hook ", StringComparison.Ordinal)))
            TestContext.Current.TestOutputHelper?.WriteLine(l.Trim());

        // The target is for the warm app: emoji the popup has drawn before (or pre-rendered at startup).
        var line = lines.Last(l => l.Contains("keystroke to popup rendered:", StringComparison.Ordinal));
        var p95 = double.Parse(P95().Match(line).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // The target is for the shipped (Release, Native AOT) build: run with INLAY_EXE pointing at the publish
        // output. A Debug build is measured and reported, but its numbers aren't held to the target.
        if (!InlayApp.IsReleaseBuild)
            Assert.Skip($"Debug build measured, not asserted ({line.Trim()}); set INLAY_EXE to the published Inlay.exe.");
        Assert.True(p95 < 16_000, line);
    }

    [GeneratedRegex(@"p95=([0-9.]+)")]
    private static partial Regex P95();
}
