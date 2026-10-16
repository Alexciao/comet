using Comet.App.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Comet.App.UI.Pages;

/// <summary>Pieces shared by the Snippets and Auto-replace pages.</summary>
internal static class PageHelpers
{
    /// <summary>A list entry: header, one-line description, optional leading control, Edit and "More" (Duplicate, Delete).</summary>
    public static SettingsCard EntryCard(string header, string description, string? warning, UIElement? leading, Action edit, Action duplicate, Action delete)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (warning is not null)
        {
            var icon = new FontIcon { Glyph = "", FontSize = 16, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"], Margin = new Thickness(0, 0, 8, 0) };
            ToolTipService.SetToolTip(icon, warning);
            AutomationProperties.SetName(icon, warning);
            actions.Children.Add(icon);
        }

        if (leading is not null)
            actions.Children.Add(leading);

        var editButton = IconButton("", $"Edit {header}");
        editButton.Click += (_, _) => edit();
        actions.Children.Add(editButton);

        var more = IconButton("", $"More options for {header}");
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var dup = new MenuFlyoutItem { Text = "Duplicate", Icon = new FontIcon { Glyph = "" } };
        dup.Click += (_, _) => duplicate();
        var del = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
        del.Click += (_, _) => delete();
        menu.Items.Add(dup);
        menu.Items.Add(del);
        more.Flyout = menu;
        actions.Children.Add(more);

        return new SettingsCard { Header = header, Description = description, Content = actions, MinHeight = 60 };
    }

    private static Button IconButton(string glyph, string name)
    {
        // A subtle (borderless) icon button, as used for row actions in Windows 11 Settings.
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>One line of text, whitespace collapsed, for list previews (the card trims it with an ellipsis).</summary>
    public static string OneLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? trimmed : trimmed[..newline] + " …";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    public static async Task<string?> PickOpenAsync(Window owner)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickSaveAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id) { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public static bool Matches(string query, params string[] fields) =>
        query.Length == 0 || fields.Any(f => f.Contains(query, StringComparison.CurrentCultureIgnoreCase));
}
