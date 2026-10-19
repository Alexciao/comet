using System.Runtime.InteropServices;
using Comet.Platform;
using Comet.Platform.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Comet.App;

/// <summary>Entry point: single instance, crash safety, then the WinUI application.</summary>
public static partial class Program
{
    /// <summary>Window class of the hidden message window; a second instance finds the first through it.</summary>
    internal const string MessageWindowClass = "Comet.MessageWindow";

    /// <summary>Posted to the running instance by a second launch: open Settings.</summary>
    internal const uint WmOpenSettings = User32.WM_APP + 100;

    /// <summary>Posted by the installer (before an update) and by tests: quit cleanly.</summary>
    internal const uint WmQuit = User32.WM_APP + 101;

    [LibraryImport("Microsoft.ui.xaml.dll")]
    private static partial void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        if (options.Quit)
            return QuitRunningInstance();
        CrashHandler.Install();

        // Local\ = per logon session, so each signed-in user gets their own Comet.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Comet.SingleInstance.6C1D3E2A", out var first);
        if (!first)
        {
            var existing = User32.FindWindowW(MessageWindowClass, null);
            if (existing != 0 && !options.Background)
            {
                User32.GetWindowThreadProcessId(existing, out var pid);
                User32.AllowSetForegroundWindow(pid);
                User32.PostMessageW(existing, WmOpenSettings, 0, 0);
            }

            return 0;
        }

        Log.Info($"Comet {AppPaths.Version} starting (pid {Environment.ProcessId}, args: {string.Join(' ', args)})");
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        App.Options = options;
        Application.Start(static parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(); // Application.Current keeps the instance alive
        });

        Log.Info("Comet stopped");
        Log.Flush();
        return 0;
    }
}

public static partial class Program
{
    /// <summary>
    /// <c>Comet.exe --quit</c> (used by the installer before replacing files): asks the running instance to quit
    /// cleanly and waits up to 5 s for it to exit. Exit code 0 when no instance is left running.
    /// </summary>
    private static int QuitRunningInstance()
    {
        var existing = User32.FindWindowW(MessageWindowClass, null);
        if (existing == 0)
            return 0;
        User32.GetWindowThreadProcessId(existing, out var pid);
        User32.PostMessageW(existing, WmQuit, 0, 0);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.WaitForExit(5000) ? 0 : 1;
        }
        catch (ArgumentException)
        {
            return 0; // already gone
        }
    }
}

/// <summary>Command-line options.</summary>
/// <param name="Background">Start in the tray without opening Settings (used by the start-at-login entry).</param>
/// <param name="AcceptInjectedInput">End-to-end tests: treat other programs' synthetic keystrokes as typing.</param>
/// <param name="Quit">Ask the running instance to quit, then exit (installer).</param>
internal sealed record LaunchOptions(bool Background, bool AcceptInjectedInput, bool Quit = false)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return new LaunchOptions(
            Background: Has("--background"),
            AcceptInjectedInput: Has("--accept-injected-input") || Environment.GetEnvironmentVariable("COMET_ACCEPT_INJECTED_INPUT") == "1",
            Quit: Has("--quit"));
    }
}
