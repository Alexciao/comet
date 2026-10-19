using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Settings.Dialogs;

/// <summary>
/// Validation shown inline under a dialog field: the first error in the system critical color, otherwise the first
/// warning in secondary text. Each field has one TextBlock of each kind (their colors come from XAML).
/// </summary>
internal static class FieldMessage
{
    public static void Show(TextBlock error, TextBlock warning, IReadOnlyList<ValidationMessage> messages, ValidationField field, bool hide)
    {
        string? errorText = null, warningText = null;
        if (!hide)
        {
            foreach (var m in messages)
            {
                if (m.Field != field)
                    continue;
                if (m.Severity == Severity.Error)
                    errorText ??= m.Text;
                else
                    warningText ??= m.Text;
            }
        }

        Set(error, errorText);
        Set(warning, errorText is null ? warningText : null);
    }

    private static void Set(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
