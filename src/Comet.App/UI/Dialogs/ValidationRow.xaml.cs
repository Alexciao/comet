using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Dialogs;

public sealed partial class ValidationRow : UserControl
{
    public ValidationRow() => InitializeComponent();

    internal ValidationRow(ValidationMessage message)
        : this()
    {
        var error = message.Severity == Severity.Error;
        ErrorIcon.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        WarningIcon.Visibility = error ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message.Text;
    }

    /// <summary>Replaces the messages shown in <paramref name="host"/>.</summary>
    internal static void Show(StackPanel host, IReadOnlyList<ValidationMessage> messages)
    {
        host.Children.Clear();
        foreach (var m in messages)
            host.Children.Add(new ValidationRow(m));
        host.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
