using System.Diagnostics;
using Inlay.App.Services;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.Core.Settings;
using Inlay.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>Settings › General: startup, the : menu, updates, backup and about.</summary>
public sealed partial class GeneralPage : SettingsPage
{
    private bool _loading;
    private bool _dialogOpen;

    public GeneralPage()
    {
        InitializeComponent();
        if (AppPaths.IsPortable)
            StartupCard.Description = "Start Inlay from this folder when you sign in to Windows";
    }

    protected override void OnAttached()
    {
        ShowProblems();
        OnAppUpdateChanged();
        Load();
        AfterFirstFrame(() =>
        {
            FindName(nameof(LaterSections));
            VersionText.Text = $"Version {AppPaths.Version}";
            if (ReleaseNotes.Installed is { } notes)
            {
                NotesCard.Header = $"What's new in {AppPaths.Version}";
                InstalledNotes.Notes = notes;
            }
            else
            {
                NotesCard.Visibility = Visibility.Collapsed; // a local build has no notes of its own
            }
            RepositoryCard.Description = AppPaths.RepositoryUrl.Host + AppPaths.RepositoryUrl.AbsolutePath;
            Load();
        });
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (!parts.HasFlag(SettingsParts.Settings))
            return;
        Load();
    }

    /// <summary>Shows the current settings (also after a hand edit of settings.json).</summary>
    private void Load()
    {
        _loading = true;
        StartupToggle.IsOn = Settings.StartAtLogin;
        if (LaterSections is not null)
        {
            EmojiUpdatesToggle.IsOn = Settings.UpdateEmojiAutomatically;
            EmojiUpdatesCard.Description = $"Checks quietly at launch and once a day · {Host.EmojiDataDescription()}";
            AppUpdatesToggle.IsOn = Settings.NotifyAppUpdates;
            AppUpdatesCard.Description = $"Checks quietly at launch and once a day · Last checked {AppHost.FormatDate(Host.AppUpdateLastChecked)}";
        }

        _loading = false;
    }

    protected override void OnAppUpdateChanged()
    {
        if (Host.AvailableUpdate is not { } release)
        {
            if (UpdateBar is not null)
                UpdateBar.IsOpen = false;
            return;
        }

        UpdateBar ??= (InfoBar)FindName(nameof(UpdateBar));
        UpdateBar.Title = $"Inlay {release.DisplayVersion} is available";
        UpdateBar.Message = $"You have version {AppPaths.Version}";
        UpdateBar.IsOpen = true;
    }

    private void ShowProblems()
    {
        var problems = Store.Problems;
        if (problems.Count == 0)
            return;
        var (file, status, copy) = problems[^1];
        ProblemsBar ??= (InfoBar)FindName(nameof(ProblemsBar));
        ProblemsBar.Title = status == LoadStatus.RecoveredFromBackup ? $"{file} was damaged" : $"{file} couldn't be read";
        ProblemsBar.Message = status == LoadStatus.RecoveredFromBackup
            ? $"Inlay restored the previous version. The damaged file was kept as {Path.GetFileName(copy)}."
            : $"Inlay started without it. The unreadable file was kept as {Path.GetFileName(copy)}.";
        ProblemsBar.IsOpen = true;
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusBar ??= (InfoBar)FindName(nameof(StatusBar));
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.StartAtLogin == StartupToggle.IsOn)
            return;
        Settings.StartAtLogin = StartupToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnEmojiUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.UpdateEmojiAutomatically == EmojiUpdatesToggle.IsOn)
            return;
        Settings.UpdateEmojiAutomatically = EmojiUpdatesToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnAppUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.NotifyAppUpdates == AppUpdatesToggle.IsOn)
            return;
        Settings.NotifyAppUpdates = AppUpdatesToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnDownloadUpdate(object sender, RoutedEventArgs e) => Host.OpenUpdatePage();

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await FilePickers.SaveBackupAsync(OwnerWindow, "Inlay backup").ConfigureAwait(true);
            if (path is null)
                return;
            var json = ImportExport.ExportBackup(Settings, Store.Snippets, Store.Shorthands, Store.Images);
            var images = Store.Images.ToList();
            var library = Store.ImageLibraryDirectory;
            await Task.Run(() =>
            {
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                ImportExport.ExportBackupArchive(file, json, images, library);
            }).ConfigureAwait(true);
            ShowStatus(InfoBarSeverity.Success, $"Saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex))
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't save the backup. {ex.Message}");
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var path = await FilePickers.OpenBackupAsync(OwnerWindow).ConfigureAwait(true);
            if (path is null)
                return;
            var library = Store.ImageLibraryDirectory;
            var backup = await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                return ImportExport.ImportBackupFile(file, library);
            }).ConfigureAwait(true);
            var confirm = new ReplaceSettingsDialog(
                $"Your settings, {Count(Store.Snippets.Count, "snippet")}, {Count(Store.Images.Count, "image")} and {Count(Store.Shorthands.Count, "shorthand")} will be replaced with the backup's {Count(backup.Snippets!.Count, "snippet")}, {Count(backup.Images!.Count, "image")} and {Count(backup.Shorthands!.Count, "shorthand")}.")
            {
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
            Store.Restore(backup);
            ShowStatus(InfoBarSeverity.Success, "Settings imported");
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex) || ex is FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, ex is FormatException
                ? "This file isn't an Inlay backup. Choose a file made with Export settings"
                : $"Couldn't read the backup. {ex.Message}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private void OnOpenRepository(object sender, RoutedEventArgs e) => AppHost.OpenInBrowser(AppPaths.RepositoryUrl);

    private void OnOpenReleases(object sender, RoutedEventArgs e) => AppHost.OpenInBrowser(AppPaths.ReleasesUrl);

    private void OnWhatsNew(object sender, RoutedEventArgs e) => _ = ShowWhatsNewAsync();

    /// <summary>
    /// The newer version's notes (fetched with the update check) in a dialog over Settings. Also opened from the
    /// tray's "Update available" item.
    /// </summary>
    internal async Task ShowWhatsNewAsync()
    {
        if (_dialogOpen || XamlRoot is null || Host.AvailableUpdate is not { } release)
            return;
        _dialogOpen = true;
        try
        {
            await new WhatsNewDialog(release, XamlRoot).ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnOpenLicense(object sender, RoutedEventArgs e) => AppHost.OpenInBrowser(AppPaths.LicenseUrl);

    private async void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Log.Directory);
                Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true })?.Dispose();
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't open the logs folder. {ex.Message}");
        }
    }
}
