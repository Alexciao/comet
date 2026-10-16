using Comet.Platform;

namespace Comet.App;

/// <summary>
/// Last line of defence. Every unhandled exception is written to a crash log. UI exceptions are survived
/// (the hook lives on its own thread and keeps working); if they keep coming, Comet shuts down cleanly so
/// the hook is removed. A process that dies outright loses its hooks with it, so keys are never left swallowed.
/// </summary>
internal static class CrashHandler
{
    private const int MaxRecoverableInWindow = 3;
    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(1);
    private static readonly Queue<DateTime> s_recent = new();

    /// <summary>Called to shut down cleanly (unhook, remove tray icon) when exceptions keep coming.</summary>
    public static Action? Shutdown { get; set; }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.WriteCrash(ex, e.IsTerminating ? "fatal" : "unhandled");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.WriteCrash(e.Exception, "unobserved task");
            e.SetObserved();
        };
    }

    /// <summary>Handles an exception from the UI thread. Returns true when the app can keep running.</summary>
    public static bool OnUiException(Exception ex)
    {
        Log.WriteCrash(ex, "ui");
        lock (s_recent)
        {
            var now = DateTime.UtcNow;
            s_recent.Enqueue(now);
            while (s_recent.Count > 0 && now - s_recent.Peek() > s_window)
                s_recent.Dequeue();
            if (s_recent.Count <= MaxRecoverableInWindow)
                return true;
        }

        Log.Warn("too many UI exceptions; shutting down cleanly");
        Shutdown?.Invoke();
        return false;
    }
}
