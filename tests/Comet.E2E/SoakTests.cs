using System.Diagnostics;
using System.Runtime.InteropServices;
using Comet.E2E.Harness;

namespace Comet.E2E;

/// <summary>
/// Soak: thousands of picker sessions and replacements in a row; Comet's memory, handles and GDI/USER objects
/// must stay flat. Runs only with <c>COMET_SOAK=&lt;count&gt;</c> (≥ 10,000 for the release check; it takes about
/// 20 minutes), ideally against the published build (<c>COMET_EXE</c>).
/// </summary>
[Collection(Desktop.Name)]
public sealed partial class SoakTests : IDisposable
{
    private readonly CometApp _comet = new CometApp().WithAutoReplace(("cmq", "comunque"));
    private EdgePage? _edge;

    public void Dispose()
    {
        _edge?.Dispose();
        _comet.Dispose();
    }

    private sealed record Sample(int Triggers, double PrivateMb, double WorkingSetMb, int Handles, int Gdi, int User);

    private Sample Measure(int triggers)
    {
        using var p = Process.GetProcessById(_comet.ProcessId);
        return new Sample(triggers, p.PrivateMemorySize64 / 1048576.0, p.WorkingSet64 / 1048576.0, p.HandleCount,
            (int)GetGuiResources(p.Handle, 0), (int)GetGuiResources(p.Handle, 1)); // signed: counts can go down
    }

    [Fact]
    public void Ten_thousand_triggers_keep_memory_and_handles_flat()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("COMET_SOAK"), out var total) || total <= 0)
            Assert.Skip("Set COMET_SOAK to the number of triggers (10000 for the release check).");

        _comet.Start();
        Wait.Until(() => _comet.Log.Contains("emoji bitmaps ready", StringComparison.Ordinal), TimeSpan.FromSeconds(60), "emoji warm-up");
        _edge = new EdgePage();
        var field = _edge.Textarea;
        field.Focus();

        string[] queries = ["smi", "hea", "thu", "fir", "roc", "tad", "joy", "cof", "piz", "dog", "cat", "sun", "moo", "sta", "par"];
        var samples = new List<Sample>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            if (i % 1000 == 0)
            {
                samples.Add(Measure(i));
                Keyboard.SelectAllAndDelete(); // keep the field short
            }

            var q = queries[i % queries.Length];
            switch (i % 10)
            {
                case 0:
                    Keyboard.Type(" cmq "); // auto-replace
                    break;
                case 1:
                case 2:
                    Keyboard.Type(" :" + q); // pick with Enter (inserts an emoji)
                    _comet.WaitForPopup();
                    Keyboard.Press(Keyboard.VK_RETURN);
                    _comet.WaitForPopupClosed();
                    break;
                default:
                    Keyboard.Type(" :" + q); // open, browse, cancel
                    _comet.WaitForPopup();
                    Keyboard.Press(Keyboard.VK_DOWN);
                    Keyboard.Press(Keyboard.VK_ESCAPE);
                    _comet.WaitForPopupClosed();
                    break;
            }
        }

        samples.Add(Measure(total));
        Assert.True(_comet.IsRunning, "Comet exited during the soak");
        var report = string.Join(Environment.NewLine, samples.Select(s =>
            FormattableString.Invariant($"{s.Triggers,6} triggers: private {s.PrivateMb,6:F1} MB, working set {s.WorkingSetMb,6:F1} MB, handles {s.Handles,5}, GDI {s.Gdi,4}, USER {s.User,4}")));
        report += Environment.NewLine + $"{total} triggers in {clock.Elapsed.TotalMinutes:F1} min";
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "comet-soak.txt"), report);

        // Compare against the first sample after things settled (1,000 triggers: caches and pools are warm).
        var baseline = samples[1];
        var last = samples[^1];
        Assert.True(last.PrivateMb - baseline.PrivateMb < 25, report);
        Assert.True(last.Handles - baseline.Handles < 100, report);
        Assert.True(last.Gdi - baseline.Gdi < 50 && last.User - baseline.User < 50, report);
        Assert.DoesNotContain("[ERROR]", _comet.Log, StringComparison.Ordinal);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint process, uint flags);
}
