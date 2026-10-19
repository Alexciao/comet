using System.Collections.ObjectModel;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.Core.Settings;
using Inlay.Platform.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using KeyShortcut = Inlay.Core.Input.Shortcut;

namespace Inlay.App.UI.Settings;

/// <summary>
/// A keyboard shortcut setting, as in PowerToys: the current shortcut as key caps and a pencil button that opens
/// <see cref="ShortcutDialog"/> to record a new one (with Reset to <see cref="DefaultShortcut"/> and Clear to turn it
/// off). Saving a different shortcut updates <see cref="Shortcut"/> and raises <see cref="ShortcutChanged"/>; the
/// owner stores it and registers it.
/// </summary>
public sealed partial class ShortcutRecorder : UserControl
{
    private readonly ObservableCollection<string> _keys = [];
    private KeyShortcut _shortcut;
    private bool _dialogOpen;

    public ShortcutRecorder()
    {
        InitializeComponent();
        Show();
    }

    public ObservableCollection<string> Keys => _keys;

    /// <summary>
    /// The shortcut as stored in settings (<c>Win+Alt+I</c>; empty = off). Setting it shows it; text that isn't a
    /// shortcut shows as off. Reading it gives the canonical form.
    /// </summary>
    public string Shortcut
    {
        get => _shortcut.ToString();
        set
        {
            _shortcut = KeyShortcut.TryParse(value, out var s) ? s : KeyShortcut.None;
            Show();
        }
    }

    /// <summary>What Reset puts back.</summary>
    public string DefaultShortcut { get; set; } = AppSettings.DefaultShortcut;

    /// <summary>The dialog's title.</summary>
    public string DialogTitle { get; set; } = "Activation shortcut";

    /// <summary>
    /// Whether a combination can be registered right now (false: another app holds it). The dialog asks for each
    /// combination it would save; null skips the check.
    /// </summary>
    public Func<KeyShortcut, bool>? CanRegister { get; set; }

    /// <summary>Raised after Save with a different shortcut; read <see cref="Shortcut"/>.</summary>
    public event EventHandler? ShortcutChanged;

    /// <summary>
    /// True while the dialog is open, false after. Suspend the registered shortcut meanwhile, so its keys reach the
    /// dialog instead of firing, and the current combination tests as free.
    /// </summary>
    public event EventHandler<bool>? RecordingChanged;

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        RecordingChanged?.Invoke(this, true);
        try
        {
            var fallback = KeyShortcut.TryParse(DefaultShortcut, out var d) ? d : KeyShortcut.None;
            var dialog = new ShortcutDialog(DialogTitle, _shortcut, fallback, CanRegister) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Chosen == _shortcut)
                return;
            _shortcut = dialog.Chosen;
            Show();
            ShortcutChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _dialogOpen = false;
            RecordingChanged?.Invoke(this, false);
        }
    }

    private void Show()
    {
        var parts = ShortcutLabels.Parts(_shortcut);
        _keys.Clear();
        foreach (var part in parts)
            _keys.Add(part);
        var off = _shortcut.IsNone;
        KeysView.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        OffText.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(KeysView, string.Join(" + ", parts));
        AutomationProperties.SetName(EditButton, off ? "Set shortcut" : $"Change shortcut, {string.Join(" + ", parts)}");
    }
}
