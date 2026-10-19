using Inlay.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>Settings › Emoji: the feature switch, the default skin tone and clearing the recently used list.</summary>
public sealed partial class EmojiPage : SettingsPage
{
    private bool _loading;

    public EmojiPage()
    {
        InitializeComponent();
    }

    protected override void OnAttached() => Load();

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Picks made since the page was last shown don't raise a settings change.
        ClearButton.IsEnabled = Store.HasFrecency;
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Settings))
            Load();
    }

    private void Load()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.Emoji;
        SkinToneBox.SelectedIndex = Settings.SkinTone;
        ClearButton.IsEnabled = Store.HasFrecency;
        _loading = false;
    }

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.Features.Emoji == FeatureToggle.IsOn)
            return;
        Settings.Features.Emoji = FeatureToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnSkinToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SkinToneBox.SelectedIndex < 0 || SkinToneBox.SelectedIndex == Settings.SkinTone)
            return;
        Settings.SkinTone = SkinToneBox.SelectedIndex;
        Store.SaveSettings();
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        Host.ResetFrecency();
        ClearButton.IsEnabled = false;
    }
}
