using Inlay.Core.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Inlay.App.UI;

/// <summary>
/// Release notes in a selectable <see cref="RichTextBlock"/>: the Markdown subset of <see cref="Markdown"/> as
/// headings, lists, bold, italic, code and links that open in the browser. The heading sizes are set from XAML
/// (<c>HeadingFontSize="{StaticResource SubtitleTextBlockFontSize}"</c>), because code never looks resources up;
/// colors and the body text come from the RichTextBlock's theme defaults.
/// </summary>
public sealed partial class ReleaseNotesView : UserControl
{
    // Vertical rhythm, as in the Settings pages: a little air above headings, less between items.
    private const double HeadingSpacing = 16;
    private const double ParagraphSpacing = 8;
    private const double ItemSpacing = 4;
    private const double ListIndent = 16;

    private static readonly FontFamily s_code = new("Cascadia Mono, Consolas");

    private readonly RichTextBlock _text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private string? _notes;
    private double _headingFontSize = 20;
    private double _subheadingFontSize = 14;

    public ReleaseNotesView() => Content = _text;

    /// <summary>The notes, in Markdown.</summary>
    public string? Notes
    {
        get => _notes;
        set
        {
            _notes = value;
            Render();
        }
    }

    /// <summary>Headings of level 1 and 2 ("What's new"); the Subtitle text size.</summary>
    public double HeadingFontSize
    {
        get => _headingFontSize;
        set
        {
            _headingFontSize = value;
            Render();
        }
    }

    /// <summary>Headings of level 3 and below ("Added", "Fixed"); the BodyStrong text size.</summary>
    public double SubheadingFontSize
    {
        get => _subheadingFontSize;
        set
        {
            _subheadingFontSize = value;
            Render();
        }
    }

    private void Render()
    {
        _text.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(_notes))
            return;
        var first = true;
        foreach (var block in Markdown.Parse(_notes))
        {
            var paragraph = new Paragraph();
            var spacing = ParagraphSpacing;
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    paragraph.FontSize = block.Level <= 2 ? _headingFontSize : _subheadingFontSize;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    spacing = HeadingSpacing;
                    break;
                case MarkdownBlockKind.ListItem:
                    paragraph.Inlines.Add(new Run { Text = block.Marker + " " });
                    spacing = ItemSpacing;
                    break;
                case MarkdownBlockKind.Code:
                    paragraph.FontFamily = s_code;
                    break;
            }

            paragraph.Margin = new Thickness(block.Kind == MarkdownBlockKind.ListItem ? ListIndent * block.Level : 0, first ? 0 : spacing, 0, 0);
            foreach (var run in block.Runs)
                Add(paragraph.Inlines, run);
            _text.Blocks.Add(paragraph);
            first = false;
        }
    }

    private static void Add(InlineCollection inlines, MarkdownRun run)
    {
        var target = inlines;
        if (run.Link is { } uri)
        {
            var link = new Hyperlink();
            link.Click += (_, _) => AppHost.OpenInBrowser(uri);
            inlines.Add(link);
            target = link.Inlines;
        }

        var lines = run.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                target.Add(new LineBreak());
            var text = new Run { Text = lines[i] };
            if (run.Style.HasFlag(MarkdownStyle.Bold))
                text.FontWeight = FontWeights.SemiBold;
            if (run.Style.HasFlag(MarkdownStyle.Italic))
                text.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (run.Style.HasFlag(MarkdownStyle.Code))
                text.FontFamily = s_code;
            target.Add(text);
        }
    }
}
