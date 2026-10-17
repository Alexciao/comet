using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Controls;

/// <summary>
/// A settings card that is itself a button (opens a page, a folder or a link), with a chevron on the right as in
/// Windows 11 Settings. Templated in UI/Styles.xaml; its hover and pressed states are the button's own.
/// </summary>
public sealed partial class SettingsLinkCard : Button
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsLinkCard), new PropertyMetadata("", OnHeaderChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsLinkCard), new PropertyMetadata("", OnDescriptionChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsLinkCard), new PropertyMetadata("", OnGlyphChanged));

    /// <summary>The trailing icon: a chevron by default; an "open externally" arrow for links that leave the app.</summary>
    public static readonly DependencyProperty ActionGlyphProperty =
        DependencyProperty.Register(nameof(ActionGlyph), typeof(string), typeof(SettingsLinkCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionVisibilityProperty =
        DependencyProperty.Register(nameof(DescriptionVisibility), typeof(Visibility), typeof(SettingsLinkCard), new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty GlyphVisibilityProperty =
        DependencyProperty.Register(nameof(GlyphVisibility), typeof(Visibility), typeof(SettingsLinkCard), new PropertyMetadata(Visibility.Collapsed));

    public SettingsLinkCard() => DefaultStyleKey = typeof(SettingsLinkCard);

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string ActionGlyph
    {
        get => (string)GetValue(ActionGlyphProperty);
        set => SetValue(ActionGlyphProperty, value);
    }

    public Visibility DescriptionVisibility
    {
        get => (Visibility)GetValue(DescriptionVisibilityProperty);
        private set => SetValue(DescriptionVisibilityProperty, value);
    }

    public Visibility GlyphVisibility
    {
        get => (Visibility)GetValue(GlyphVisibilityProperty);
        private set => SetValue(GlyphVisibilityProperty, value);
    }

    // The header is drawn by the template, so screen readers get it as the button's name.
    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(d, e.NewValue as string ?? "");

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingsLinkCard)d).DescriptionVisibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingsLinkCard)d).GlyphVisibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;
}
