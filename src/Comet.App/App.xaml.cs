using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;

namespace Comet.App;

[SuppressMessage("Design", "CA1001", Justification = "The host lives as long as the process and is disposed by AppHost.Shutdown.")]
public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
        InitializeComponent();
        // Screenshots of both themes without changing the system setting (COMET_THEME=Light|Dark).
        if (Enum.TryParse<ApplicationTheme>(Environment.GetEnvironmentVariable("COMET_THEME"), ignoreCase: true, out var theme))
            RequestedTheme = theme;
        UnhandledException += (_, e) => e.Handled = CrashHandler.OnUiException(e.Exception);
    }

    /// <summary>Set by <see cref="Program"/> before the application starts.</summary>
    internal static LaunchOptions Options { get; set; } = new(false, false);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Comet lives in the tray: closing Settings must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _host = new AppHost(this, Options);
        CrashHandler.Shutdown = _host.Shutdown;
        _host.Start();
    }
}
