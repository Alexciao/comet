using System.Diagnostics;
using Comet.Platform;
using Comet.Platform.Context;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Comet.App.UI.Settings;

/// <summary>
/// Running apps and program details for the Excluded apps lists, always gathered off the UI thread. The list of
/// running apps is reused for a few seconds (both lists and the Add app dialog ask for it); icons are cached by
/// <see cref="AppIcons"/>.
/// </summary>
internal static class AppCatalog
{
    private static readonly TimeSpan s_fresh = TimeSpan.FromSeconds(15);
    private static Task<List<ProgramInfo>>? s_running;
    private static long s_runningAt;

    /// <summary>Header icons are 20 DIPs; extracted at the display's scale so they stay sharp.</summary>
    public const double IconSize = 20;

    public static Task<List<ProgramInfo>> RunningAsync()
    {
        if (s_running is null || Stopwatch.GetElapsedTime(s_runningAt) > s_fresh)
        {
            s_runningAt = Stopwatch.GetTimestamp();
            s_running = Task.Run(() =>
            {
                var started = Stopwatch.GetTimestamp();
                var apps = RunningApps.WithWindows();
                Log.Debug($"running apps: {apps.Count} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                return apps;
            });
        }

        return s_running;
    }

    /// <summary>Describes an excluded app: from a remembered path, else from a running copy, else by name only.</summary>
    public static async Task<ProgramInfo> ResolveAsync(string fileName, string? knownPath)
    {
        if (knownPath is not null && await Task.Run(() => File.Exists(knownPath)).ConfigureAwait(false))
            return await Task.Run(() => ProgramInfo.FromPath(knownPath)).ConfigureAwait(false);
        var running = await RunningAsync().ConfigureAwait(false);
        return running.FirstOrDefault(p => string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? ProgramInfo.FromFileName(fileName);
    }

    /// <summary>Extracts the icon in the background and shows it on <paramref name="item"/>.</summary>
    public static void LoadIcon(AppItem item, double scale)
    {
        var size = (int)Math.Round(IconSize * scale);
        var path = item.Program.Path;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            var pixels = AppIcons.ForFile(path, size);
            dispatcher.TryEnqueue(() => item.SetIcon(pixels));
        });
    }

    /// <summary>Lets the user pick a program file. Returns its path, or null.</summary>
    public static async Task<string?> BrowseAsync(XamlRoot root)
    {
        var picker = new FileOpenPicker(root.ContentIslandEnvironment.AppWindowId) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
