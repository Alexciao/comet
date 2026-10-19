using System.Diagnostics;
using System.Globalization;
using Comet.Platform;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Comet.App.UI.Settings;

/// <summary>
/// Settings timings for docs/performance.md, logged only when <c>COMET_PERF=1</c>: the time from an action to the
/// frame that shows its result, and the frame intervals while a list scrolls. Off, it costs a field read.
/// </summary>
internal static class PerfProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("COMET_PERF") == "1";

    /// <summary>
    /// Logs how long it takes from now until the next frame has been rendered, and how much of that was the frame
    /// itself (layout and rendering) rather than the work done before it.
    /// </summary>
    public static void UntilNextFrame(string what)
    {
        if (!Enabled)
            return;
        var started = Stopwatch.GetTimestamp();
        long frameStart = 0;
        void OnRendering(object? sender, object e)
        {
            if (frameStart == 0)
                frameStart = Stopwatch.GetTimestamp();
        }

        void OnRendered(object? sender, RenderedEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendered -= OnRendered;
            var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var frame = frameStart == 0 ? 0 : Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            Log.Info(string.Create(CultureInfo.InvariantCulture, $"perf: {what} {total:F1} ms (frame {frame:F1} ms)"));
        }

        CompositionTarget.Rendering += OnRendering;
        CompositionTarget.Rendered += OnRendered;
    }

    public static void Note(string what) => Log.Info($"perf: {what}");

    /// <summary>
    /// While <paramref name="scroller"/> moves, records how long each UI-thread frame takes (from Rendering to
    /// Rendered: layout, realizing cards, rendering). Scrolling itself runs on the compositor thread; the list keeps
    /// up, with no blank or stalled cards, as long as these frames fit in one refresh (16.7 ms at 60 Hz).
    /// </summary>
    public static void TrackScrolling(ScrollViewer scroller, string page)
    {
        if (!Enabled)
            return;
        var frames = new List<double>();
        long start = 0;
        var tracking = false;
        void OnRendering(object? sender, object e) => start = Stopwatch.GetTimestamp();
        void OnRendered(object? sender, RenderedEventArgs e)
        {
            if (start != 0)
                frames.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = 0;
        }

        scroller.ViewChanging += (_, _) =>
        {
            if (tracking)
                return;
            tracking = true;
            start = 0;
            frames.Clear();
            CompositionTarget.Rendering += OnRendering;
            CompositionTarget.Rendered += OnRendered;
        };
        scroller.ViewChanged += (_, e) =>
        {
            if (e.IsIntermediate || !tracking)
                return;
            tracking = false;
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendered -= OnRendered;
            if (frames.Count == 0)
                return;
            frames.Sort();
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"perf: scroll {page}: {frames.Count} UI frames, p50 {frames[frames.Count / 2]:F1} ms, p95 {frames[(int)(frames.Count * 0.95)]:F1} ms, max {frames[^1]:F1} ms, over 16.7 ms: {frames.Count(f => f > 16.7)}"));
        };
    }
}
