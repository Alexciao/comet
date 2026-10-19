using System.Diagnostics;
using Inlay.App.Services;
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
            StartupCard.Description = "Inlay starts from this folder, in the notification area, when you sign in";
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
            EmojiUpdatesCard.Description = Host.EmojiDataDescription();
            AppUpdatesToggle.IsOn = Settings.NotifyAppUpdates;
            AppUpdatesCard.Description = $"Checks GitHub once a week; nothing is installed for you · Last checked {AppHost.FormatDate(Host.AppUpdateLastChecked)}";
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
        UpdateBar.Message = $"You have version {AppPaths.Version}.";
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
            var json = ImportExport.ExportBackup(Settings, Store.Snippets, Store.Shorthands);
            var name = await FilePickers.SaveJsonAsync(OwnerWindow, "Inlay backup", json).ConfigureAwait(true);
            if (name is not null)
                ShowStatus(InfoBarSeverity.Success, $"Saved to {name}.");
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex))
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't export: {ex.Message}");
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var json = await FilePickers.OpenJsonAsync(OwnerWindow).ConfigureAwait(true);
            if (json is null)
                return;
            var backup = ImportExport.ImportBackup(json);
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Replace your settings?",
                Content = $"Your settings, {Store.Snippets.Count} snippets and {Store.Shorthands.Count} replacements will be replaced by the backup's {backup.Snippets!.Count} snippets and {backup.Shorthands!.Count} replacements.",
                PrimaryButtonText = "Replace",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
            Store.Restore(backup);
            ShowStatus(InfoBarSeverity.Success, "Settings imported.");
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex) || ex is FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't import: {ex.Message}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnOpenRepository(object sender, RoutedEventArgs e) => AppHost.OpenInBrowser(AppPaths.RepositoryUrl);

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
            ShowStatus(InfoBarSeverity.Error, $"Couldn't open the folder: {ex.Message}");
        }
    }
}
