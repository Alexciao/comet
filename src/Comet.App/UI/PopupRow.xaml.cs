using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI;

/// <summary>One result row, created once and updated in place: no containers are created while typing.</summary>
public sealed partial class PopupRow : UserControl
{
    public const double RowHeight = 36;

    private bool _selected;
    private bool _hover;

    public PopupRow()
    {
        InitializeComponent();
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
    }

    /// <summary>An emoji row: the emoji, then ":shortcode:" with the matched part emphasized.</summary>
    public void ShowEmoji(string emoji, string shortcode, (int Start, int Length) match)
    {
        EmojiText.Text = emoji;
        EmojiText.Visibility = Visibility.Visible;
        SnippetIcon.Visibility = Visibility.Collapsed;
        var (start, length) = match;
        BeforeRun.Text = ":" + shortcode[..start];
        MatchRun.Text = shortcode.Substring(start, length);
        AfterRun.Text = shortcode[(start + length)..] + ":";
        PreviewRun.Text = "";
    }

    /// <summary>A snippet row: icon, the name (matched part emphasized), then a one-line preview.</summary>
    public void ShowSnippet(string name, string preview, (int Start, int Length) match)
    {
        EmojiText.Visibility = Visibility.Collapsed;
        SnippetIcon.Visibility = Visibility.Visible;
        var (start, length) = match;
        BeforeRun.Text = name[..start];
        MatchRun.Text = name.Substring(start, length);
        AfterRun.Text = name[(start + length)..];
        PreviewRun.Text = "  " + preview;
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            UpdateVisualState();
        }
    }

    private void SetHover(bool hover)
    {
        _hover = hover;
        UpdateVisualState();
    }

    private void UpdateVisualState() =>
        VisualStateManager.GoToState(this, (_selected, _hover) switch
        {
            (true, true) => "SelectedPointerOver",
            (true, false) => "Selected",
            (false, true) => "PointerOver",
            _ => "Normal",
        }, useTransitions: false);
}
