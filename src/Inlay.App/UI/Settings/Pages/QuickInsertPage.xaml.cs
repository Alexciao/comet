using System.Globalization;
using Inlay.App.Services;
using Inlay.Core.Settings;
using Inlay.Core.Text;
using Inlay.Platform.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>
/// Settings › Quick Insert: the trigger, the keyboard shortcut, recents, creating snippets, and what it shows per
/// app. It has no switch of its own: it is on while Snippets, Images or Emoji is.
/// </summary>
public sealed partial class QuickInsertPage : SettingsPage
{
    private bool _loading;

    public QuickInsertPage()
    {
        InitializeComponent();
    }

    protected override void OnAttached()
    {
        OwnerWindow.Closing += ApplyTrigger; // a character typed but not yet confirmed still counts
        ShortcutRecorder.CanRegister = Host.CanRegisterShortcut;
        ShortcutRecorder.RecordingChanged += (_, recording) => Host.SuspendShortcut(recording);
        Host.ShortcutChanged += ShowShortcutStatus;
        OwnerWindow.Closing += () => Host.ShortcutChanged -= ShowShortcutStatus;
        Load();
        AfterFirstFrame(() => AppModes.Attach(Store));
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Picks made since the page was last shown don't raise a settings change.
        ClearButton.IsEnabled = Store.HasFrecency;
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        Host.ResetFrecency();
        ClearButton.IsEnabled = false;
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (!parts.HasFlag(SettingsParts.Settings))
            return;
        Load();
        AppModes.Refresh();
    }

    /// <summary>Shows the current settings (also after a hand edit of settings.json).</summary>
    private void Load()
    {
        _loading = true;
        var f = Settings.Features;
        ShowDeferred(ref AllOffBar, nameof(AllOffBar), !f.Snippets && !f.Images && !f.Emoji);
        TriggerBox.Text = Settings.TriggerCharacter;
        TriggerCard.Description = TriggerFormat.Fill("Type {trigger} and a few letters to open Quick Insert", Settings.TriggerCharacter);
        MinCharsBox.Value = Settings.MinimumCharacters;
        RecentsToggle.IsOn = Settings.ShowRecentsOnPause;
        DelaySlider.Value = Settings.RecentsDelayMs;
        DelayCard.Description = Delay(Settings.RecentsDelayMs);
        DelayCard.IsEnabled = Settings.ShowRecentsOnPause;
        ShortcutRecorder.Shortcut = Settings.QuickInsertShortcut;
        ShowShortcutStatus();
        CreateToggle.IsOn = Settings.OfferCreateSnippet;
        CreateToggle.IsEnabled = f.Snippets;
        CreateCard.Description = f.Snippets
            ? TriggerFormat.Fill("Adds a row that starts a new snippet with the name you typed after {trigger}", Settings.TriggerCharacter)
            : "Needs Snippets to be on";
        _loading = false;
    }

    /// <summary>The card says when the shortcut couldn't be registered (another app holds it).</summary>
    private void ShowShortcutStatus() => ShortcutCard.Description = Host.ShortcutStatus switch
    {
        ShortcutRegistration.InUse => "Another app is using this shortcut. Choose a different one",
        ShortcutRegistration.Failed => "Couldn't turn on this shortcut. Choose a different one",
        _ => "Open Quick Insert without typing the trigger",
    };

    private void OnShortcutChanged(object? sender, EventArgs e)
    {
        if (_loading || Settings.QuickInsertShortcut == ShortcutRecorder.Shortcut)
            return;
        Settings.QuickInsertShortcut = ShortcutRecorder.Shortcut;
        Store.SaveSettings();
    }

    private static string Delay(int ms) => string.Create(CultureInfo.CurrentCulture, $"{ms} ms");

    private void OnTriggerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        ApplyTrigger();
        e.Handled = true;
    }

    private void OnTriggerLostFocus(object sender, RoutedEventArgs e) => ApplyTrigger();

    /// <summary>Accepts one character that isn't a letter, digit or space; anything else goes back to the current one.</summary>
    private void ApplyTrigger()
    {
        if (_loading)
            return;
        var text = TriggerBox.Text.Trim();
        var valid = text.Length == 1 && !char.IsLetterOrDigit(text[0]) && !char.IsSurrogate(text[0]);
        if (!valid || text == Settings.TriggerCharacter)
        {
            TriggerBox.Text = Settings.TriggerCharacter;
            return;
        }

        Settings.TriggerCharacter = text;
        Store.SaveSettings();
    }

    private void OnMinCharsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
            return;
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = Settings.MinimumCharacters; // cleared: put the current value back
            return;
        }

        var value = (int)Math.Clamp(Math.Round(args.NewValue, MidpointRounding.AwayFromZero), 1, AppSettings.MaxMinimumCharacters);
        if (value != args.NewValue)
            sender.Value = value; // show what is stored (2.5 → 3), so the spin buttons step from there
        if (value == Settings.MinimumCharacters)
            return;
        Settings.MinimumCharacters = value;
        Store.SaveSettings();
    }

    private void OnRecentsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.ShowRecentsOnPause == RecentsToggle.IsOn)
            return;
        Settings.ShowRecentsOnPause = RecentsToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnDelayChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue / 100) * 100;
        DelayCard.Description = Delay(value);
        if (_loading || value == Settings.RecentsDelayMs)
            return;
        Settings.RecentsDelayMs = value;
        Store.SaveSettings();
    }

    private void OnCreateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.OfferCreateSnippet == CreateToggle.IsOn)
            return;
        Settings.OfferCreateSnippet = CreateToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnOpenSnippets(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) => OwnerWindow.Navigate("snippets");

    private void OnOpenImages(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) => OwnerWindow.Navigate("images");

    private void OnOpenEmoji(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) => OwnerWindow.Navigate("emoji");
}
