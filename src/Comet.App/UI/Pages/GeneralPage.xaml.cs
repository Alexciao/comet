using System.Globalization;
using Comet.App.Services;
using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class GeneralPage : UserControl
{
    private static readonly string[] s_tones = ["Default ✋", "Light ✋🏻", "Medium-light ✋🏼", "Medium ✋🏽", "Medium-dark ✋🏾", "Dark ✋🏿"];

    private readonly AppHost _host;
    private bool _loading;

    internal GeneralPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var tone in s_tones)
            SkinToneBox.Items.Add(tone);
        Load();
        _host.Settings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => _host.Settings.Changed -= OnSettingsChanged;
    }

    private AppSettings Settings => _host.Settings.Settings;

    private void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Settings))
            Load();
    }

    /// <summary>Shows the current settings (also after a hand edit of settings.json).</summary>
    internal void Load()
    {
        _loading = true;
        try
        {
            var s = Settings;
            EmojiToggle.IsOn = s.Features.Emoji;
            SnippetsToggle.IsOn = s.Features.Snippets;
            AutoReplaceToggle.IsOn = s.Features.AutoReplace;
            StartAtLoginToggle.IsOn = s.StartAtLogin;
            TriggerBox.Text = s.TriggerCharacter;
            MinCharsBox.SelectedIndex = s.MinimumCharacters - 1;
            SkinToneBox.SelectedIndex = s.SkinTone;
            UpdatesToggle.IsOn = s.UpdateEmojiAutomatically;
            UpdatesCard.Description = _host.EmojiDataDescription();
            ShowProblems();
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowProblems()
    {
        var problems = _host.Settings.Problems;
        if (problems.Count == 0)
            return;
        var (file, status, copy) = problems[^1];
        ProblemsBar.Title = status == LoadStatus.RecoveredFromBackup ? $"{file} was damaged" : $"{file} couldn't be read";
        ProblemsBar.Message = status == LoadStatus.RecoveredFromBackup
            ? $"Comet restored the previous version. The damaged file was kept as {Path.GetFileName(copy)}."
            : $"Comet started with default values. The unreadable file was kept as {Path.GetFileName(copy)}.";
        ProblemsBar.IsOpen = true;
    }

    private void Save()
    {
        if (!_loading)
            _host.Settings.SaveSettings();
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

    private void OnTriggerChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var text = TriggerBox.Text.Trim();
        Settings.TriggerCharacter = text.Length == 1 ? text : ":";
        Save();
        TriggerBox.Text = Settings.TriggerCharacter;
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

    private void OnResetFrecency(object sender, RoutedEventArgs e)
    {
        var confirm = new Button { Content = "Reset", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Forget which emoji and snippets you pick most?", Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] },
                    confirm,
                },
            },
        };
        confirm.Click += (_, _) =>
        {
            _host.ResetFrecency();
            flyout.Hide();
        };
        flyout.ShowAt(ResetFrecencyButton);
    }

    internal static string FormatDate(DateTimeOffset? date) =>
        date is { } d ? d.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "never";
}
