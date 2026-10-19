using Inlay.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Inlay.App.UI;

/// <summary>
/// One Quick Insert row, created once and updated in place: no containers are created while typing. Two stacked
/// lines — the code with the matched part emphasized, and a caption (an emoji's name, a snippet's first line, an
/// image's details) — beside a glyph slot, all within the fixed row height.
/// </summary>
public sealed partial class QuickInsertRow : UserControl
{
    public const double RowHeight = 36;

    /// <summary>Size of the emoji glyph and of the square it is drawn in (shared with <see cref="EmojiBitmapCache"/>).</summary>
    public const double EmojiFontSize = 20;
    public const double EmojiBox = 28;

    /// <summary>Size of an image thumbnail in the glyph slot.</summary>
    public const double ThumbnailBox = 32;

    private const string SnippetGlyph = "";
    private const string AddGlyph = "";
    private const string ImageGlyph = "";

    private enum Slot
    {
        Emoji,
        EmojiText,
        Symbol,
        Icon,
        Thumbnail,
    }

    private bool _selected;
    private bool _hover;
    private Slot _slot = Slot.Emoji;

    public QuickInsertRow()
    {
        InitializeComponent();
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
    }

    /// <summary>
    /// An emoji row. The emoji is drawn from a pre-rendered <paramref name="bitmap"/> when available (cheap),
    /// otherwise as text.
    /// </summary>
    public void ShowEmoji(string emoji, ImageSource? bitmap, string trigger, string shortcode, (int Start, int Length) match, string caption)
    {
        if (bitmap is not null)
        {
            EmojiImage.Source = bitmap;
            ShowSlot(Slot.Emoji);
        }
        else
        {
            EmojiText.Text = emoji;
            ShowSlot(Slot.EmojiText);
        }

        SetCode(trigger, shortcode, match);
        SetCaption(caption);
    }

    /// <summary>A symbol row: the symbol as text, its code, and its name.</summary>
    public void ShowSymbol(string symbol, string trigger, string code, (int Start, int Length) match, string caption)
    {
        SymbolText.Text = symbol;
        ShowSlot(Slot.Symbol);
        SetCode(trigger, code, match);
        SetCaption(caption);
    }

    /// <summary>A snippet row: icon, the name, then the first line of its text.</summary>
    public void ShowSnippet(string trigger, string name, string preview, (int Start, int Length) match)
    {
        GlyphIcon.Glyph = SnippetGlyph;
        ShowSlot(Slot.Icon);
        SetCode(trigger, name, match);
        SetCaption(preview);
    }

    /// <summary>
    /// An image row: the thumbnail where an emoji would be (an image icon until it arrives, so nothing moves), the
    /// name, and the details ("GIF · 1.2 MB", "Newest in Screenshots · 2 min ago").
    /// </summary>
    public void ShowImage(ImageSource? thumbnail, string trigger, string name, (int Start, int Length) match, string caption)
    {
        if (thumbnail is not null)
        {
            ThumbImage.Source = thumbnail;
            ShowSlot(Slot.Thumbnail);
        }
        else
        {
            ThumbImage.Source = null;
            GlyphIcon.Glyph = ImageGlyph;
            ShowSlot(Slot.Icon);
        }

        SetCode(trigger, name, match);
        SetCaption(caption);
    }

    /// <summary>The "Create snippet :name" row: an add icon, in secondary styling.</summary>
    public void ShowCreateSnippet(string trigger, string name)
    {
        GlyphIcon.Glyph = AddGlyph;
        ShowSlot(Slot.Icon);
        BeforeRun.Text = "Create snippet ";
        MatchRun.Text = "";
        AfterRun.Text = TriggerFormat.Code(trigger, name);
        SetCaption("");
    }

    /// <summary>Shows the history icon that marks a recent item.</summary>
    public bool IsRecent
    {
        set
        {
            var visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (HistoryIcon.Visibility != visibility)
                HistoryIcon.Visibility = visibility;
        }
    }

    private void SetCode(string trigger, string code, (int Start, int Length) match)
    {
        var (start, length) = match;
        BeforeRun.Text = TriggerFormat.Code(trigger, code[..start]);
        MatchRun.Text = code.Substring(start, length);
        AfterRun.Text = code[(start + length)..];
    }

    private void SetCaption(string caption)
    {
        CaptionLine.Text = caption;
        var visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (CaptionLine.Visibility != visibility)
            CaptionLine.Visibility = visibility;
    }

    /// <summary>Shows one glyph element and collapses the one shown before (only those two are touched).</summary>
    private void ShowSlot(Slot slot)
    {
        if (slot == _slot && Element(slot).Visibility == Visibility.Visible)
            return;
        Element(_slot).Visibility = Visibility.Collapsed;
        Element(slot).Visibility = Visibility.Visible;
        _slot = slot;
    }

    private FrameworkElement Element(Slot slot) => slot switch
    {
        Slot.Emoji => EmojiImage,
        Slot.EmojiText => EmojiText,
        Slot.Symbol => SymbolText,
        Slot.Icon => GlyphIcon,
        _ => Thumb,
    };

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
