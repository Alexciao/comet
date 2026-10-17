using System.Diagnostics;
using Comet.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class AboutPage : UserControl
{
    private readonly AppHost _host;

    internal AboutPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = $"Version {AppPaths.Version}";
        DataFolderCard.Description = AppPaths.DataDirectory;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        DataCard.Description = $"Version {_host.EmojiDataVersion()} · last checked for updates {GeneralPage.FormatDate(_host.EmojiDataLastChecked)}";

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDirectory);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => OpenFolder(Log.Directory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"could not open {path}: {ex.Message}");
        }
    }
}
