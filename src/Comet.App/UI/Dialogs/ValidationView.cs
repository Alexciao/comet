using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Comet.App.UI.Dialogs;

/// <summary>Renders validation messages under a form, with the Windows 11 critical/caution icons and colors.</summary>
internal static class ValidationView
{
    public static void Show(StackPanel host, IReadOnlyList<ValidationMessage> messages)
    {
        host.Children.Clear();
        foreach (var m in messages)
        {
            var error = m.Severity == Severity.Error;
            var brush = (Brush)Application.Current.Resources[error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = error ? "" : "", FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
            host.Children.Add(row);
        }
    }
}
