using Comet.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Comet.App.UI;

public sealed partial class SettingsWindow : Window
{
    internal SettingsWindow(AppHost host)
    {
        _ = host;
        InitializeComponent();
    }

    internal void BringToFront()
    {
        Activate();
        User32.SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }
}
