using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Controls;

/// <summary>
/// One snippet or replacement in its page's list. All visuals are in the XAML; code only sets text and visibility,
/// so nothing here depends on looking theme resources up at run time.
/// </summary>
public sealed partial class EntryRow : UserControl
{
    private bool _settingSwitch;

    public EntryRow() => InitializeComponent();

    public event Action? EditRequested;

    public event Action? DuplicateRequested;

    public event Action? DeleteRequested;

    /// <summary>The per-entry on/off switch changed (auto-replace entries only).</summary>
    public event Action<bool>? EnabledChanged;

    /// <summary>Fills the row. <paramref name="enabled"/> null hides the on/off switch.</summary>
    internal void Show(string glyph, string title, string subtitle, string? warning, bool? enabled)
    {
        LeadingIcon.Glyph = glyph;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        WarningIcon.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        if (warning is not null)
        {
            ToolTipService.SetToolTip(WarningIcon, warning);
            AutomationProperties.SetName(WarningIcon, warning);
        }

        EnabledSwitch.Visibility = enabled is null ? Visibility.Collapsed : Visibility.Visible;
        if (enabled is { } on)
        {
            _settingSwitch = true;
            EnabledSwitch.IsOn = on;
            _settingSwitch = false;
            AutomationProperties.SetName(EnabledSwitch, $"Use {title}");
        }

        ToolTipService.SetToolTip(EditButton, "Edit");
        AutomationProperties.SetName(EditButton, $"Edit {title}");
        ToolTipService.SetToolTip(MoreButton, "More options");
        AutomationProperties.SetName(MoreButton, $"More options for {title}");
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => EditRequested?.Invoke();

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateRequested?.Invoke();

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();

    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingSwitch)
            EnabledChanged?.Invoke(EnabledSwitch.IsOn);
    }
}
