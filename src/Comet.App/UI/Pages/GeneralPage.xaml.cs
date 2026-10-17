using System.Globalization;
using Comet.App.Services;
using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Comet.App.UI.Pages;

public sealed partial class GeneralPage : UserControl
{
    private readonly AppHost _host;
    private readonly SettingsWindow _window;
    private bool _loading;

    internal GeneralPage(AppHost host, SettingsWindow window)
    {
        _host = host;
        _window = window;
        InitializeComponent();
    }

    private AppSettings Settings => _host.Settings.Settings;

    // Listen only while the page is shown; it reloads when it comes back.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Settings.Changed += OnSettingsChanged;
        _host.PausedChanged += OnPausedChanged;
        Load();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _host.Settings.Changed -= OnSettingsChanged;
        _host.PausedChanged -= OnPausedChanged;
    }

    private void OnSettingsChanged(SettingsParts parts) => Load();

    private void OnPausedChanged(bool paused) => Load();

    /// <summary>Shows the current settings (also after a hand edit of settings.json).</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var s = Settings;
            ActiveToggle.IsOn = !_host.Paused;
            ActiveCard.Header = _host.Paused ? "Comet is paused" : "Comet is on";
            EmojiToggle.IsOn = s.Features.Emoji;
            SnippetsToggle.IsOn = s.Features.Snippets;
            AutoReplaceToggle.IsOn = s.Features.AutoReplace;
            SnippetsCard.Description = $"Your saved text, in the same : list · {Count(_host.Settings.Snippets.Count, "snippet")}";
            AutoReplaceCard.Description = $"Abbreviations that expand as you type · {Count(_host.Settings.AutoReplace.Count, "replacement")}";
            StartAtLoginToggle.IsOn = s.StartAtLogin;
            TriggerBox.Text = s.TriggerCharacter;
            MinCharsBox.SelectedIndex = Math.Clamp(s.MinimumCharacters, 1, 5) - 1;
            SkinToneBox.SelectedIndex = Math.Clamp(s.SkinTone, 0, 5);
            UpdatesToggle.IsOn = s.UpdateEmojiAutomatically;
            UpdatesCard.Description = _host.EmojiDataDescription();
            var excluded = s.PickerExcludedApps.Count + s.AutoReplaceExcludedApps.Count;
            ExcludedCard.Description = excluded == 0 ? "Comet works in every app" : $"Comet stays out of {Count(excluded, "app")}";
            ShowProblems();
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.CurrentCulture)} {noun}s";

    private void ShowProblems()
    {
        var problems = _host.Settings.Problems;
        if (problems.Count == 0)
            return;
        var (file, status, copy) = problems[^1];
        ProblemsBar.Title = status == LoadStatus.RecoveredFromBackup ? $"{file} was damaged" : $"{file} couldn't be read";
        ProblemsBar.Message = status == LoadStatus.RecoveredFromBackup
            ? $"Comet restored the previous version. The damaged file was kept as {Path.GetFileName(copy)}."
            : $"Comet started without it. The unreadable file was kept as {Path.GetFileName(copy)}.";
        ProblemsBar.IsOpen = true;
    }

    private void Save()
    {
        if (!_loading)
            _host.Settings.SaveSettings();
    }

    private void OnActiveToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _host.SetPaused(!ActiveToggle.IsOn);
    }

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Settings.Features.Emoji = EmojiToggle.IsOn;
        Settings.Features.Snippets = SnippetsToggle.IsOn;
        Settings.Features.AutoReplace = AutoReplaceToggle.IsOn;
        Save();
    }

    private void OnStartAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Settings.StartAtLogin = StartAtLoginToggle.IsOn;
        Save();
    }

    private void OnTriggerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyTrigger();
            e.Handled = true;
        }
    }

    private void OnTriggerChanged(object sender, RoutedEventArgs e) => ApplyTrigger();

    private void ApplyTrigger()
    {
        if (_loading)
            return;
        var text = TriggerBox.Text.Trim();
        var trigger = text.Length == 1 && !char.IsLetterOrDigit(text[0]) ? text : Settings.TriggerCharacter;
        TriggerBox.Text = trigger;
        if (trigger == Settings.TriggerCharacter)
            return;
        Settings.TriggerCharacter = trigger;
        Save();
    }

    private void OnMinCharsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MinCharsBox.SelectedIndex < 0)
            return;
        Settings.MinimumCharacters = MinCharsBox.SelectedIndex + 1;
        Save();
    }

    private void OnSkinToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SkinToneBox.SelectedIndex < 0)
            return;
        Settings.SkinTone = SkinToneBox.SelectedIndex;
        Save();
    }

    private void OnUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Settings.UpdateEmojiAutomatically = UpdatesToggle.IsOn;
        Save();
    }

    private void OnExcludedClick(object sender, RoutedEventArgs e) => _window.Navigate("excluded");

    private void OnResetConfirmed(object sender, RoutedEventArgs e)
    {
        _host.ResetFrecency();
        ResetFlyout.Hide();
    }

    internal static string FormatDate(DateTimeOffset? date) =>
        date is { } d ? d.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "never";
}
