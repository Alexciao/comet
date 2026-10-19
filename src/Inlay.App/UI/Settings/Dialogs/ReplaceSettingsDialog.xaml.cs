using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>Confirms importing a backup, saying what it replaces. In XAML so it gets the default dialog style.</summary>
public sealed partial class ReplaceSettingsDialog : ContentDialog
{
    internal ReplaceSettingsDialog(string message)
    {
        InitializeComponent();
        Message.Text = message;
    }
}
