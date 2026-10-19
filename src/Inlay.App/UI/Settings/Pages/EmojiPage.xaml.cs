using Inlay.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>Settings › Emoji: the feature switch, symbols and the default skin tone.</summary>
public sealed partial class EmojiPage : SettingsPage
{
    private bool _loading;

    public EmojiPage()
    {
        InitializeComponent();
    }

    protected override void OnAttached() => Load();

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Settings))
            Load();
    }

    private void Load()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.Emoji;
        SymbolsToggle.IsOn = Settings.Features.Symbols;
        SymbolsCard.IsEnabled = Settings.Features.Emoji;
        SkinToneBox.SelectedIndex = Settings.SkinTone;
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

    private void OnSymbolsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.Features.Symbols == SymbolsToggle.IsOn)
            return;
        Settings.Features.Symbols = SymbolsToggle.IsOn;
        Store.SaveSettings();
    }
}
