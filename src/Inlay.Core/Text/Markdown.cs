using System.Text;
using System.Text.RegularExpressions;

namespace Inlay.Core.Text;

public enum MarkdownBlockKind
{
    Paragraph,
    /// <summary><see cref="MarkdownBlock.Level"/> is 1–6.</summary>
    Heading,
    /// <summary>A bullet or numbered item; <see cref="MarkdownBlock.Level"/> is the nesting depth from 0.</summary>
    ListItem,
    /// <summary>A fenced code block: one <see cref="MarkdownStyle.Code"/> run, lines separated by \n.</summary>
    Code,
}

[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>A stretch of text with one style; <see cref="Link"/> is an absolute http(s) address when it is a link.</summary>
public sealed record MarkdownRun(string Text, MarkdownStyle Style = MarkdownStyle.None, Uri? Link = null);

/// <summary>A paragraph, heading, list item or code block. <see cref="Marker"/> is "•" or "3." for list items.</summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownRun> Runs, int Level = 0, string Marker = "");

/// <summary>
/// The Markdown that release notes use, and no more: ATX headings, bullet and numbered lists, paragraphs, fenced
/// code, and inline bold, italic, code and links (also bare and &lt;angle&gt; http(s) addresses). Quotes lose their
/// marker; rules, HTML comments and link reference definitions are dropped; anything else reads as plain text.
/// Line breaks inside a paragraph are spaces, as in a Markdown file.
/// </summary>
public static partial class Markdown
{
    private const string Bullet = "•";

    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = HtmlComment().Replace(text, "").ReplaceLineEndings("\n").Split('\n');
        var pending = new StringBuilder();
        var kind = MarkdownBlockKind.Paragraph;
        var (level, marker) = (0, "");
        var listIndents = new List<int>(); // indents of the open list items, outermost first

        void Flush()
        {
            if (pending.Length > 0)
                blocks.Add(new MarkdownBlock(kind, ParseInline(pending.ToString()), level, marker));
            pending.Clear();
        }

        void Start(MarkdownBlockKind k, string content, int l = 0, string m = "")
        {
            Flush();
            (kind, level, marker) = (k, l, m);
            pending.Append(content.Trim());
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (Fence().Match(line) is { Success: true } fence)
            {
                Flush();
                var code = new List<string>();
                var close = fence.Groups[1].Value;
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(close, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, [new MarkdownRun(string.Join('\n', code), MarkdownStyle.Code)]));
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                listIndents.Clear();
                Start(MarkdownBlockKind.Heading, heading.Groups[2].Value, heading.Groups[1].Length);
                Flush();
                continue;
            }

            if (Rule().IsMatch(line) || LinkDefinition().IsMatch(line))
            {
                Flush();
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                var indent = Indent(item.Groups[1].Value);
                while (listIndents.Count > 0 && listIndents[^1] >= indent)
                    listIndents.RemoveAt(listIndents.Count - 1);
                var depth = listIndents.Count;
                listIndents.Add(indent);
                var number = item.Groups[2].Value;
                Start(MarkdownBlockKind.ListItem, item.Groups[3].Value, depth, number is "-" or "*" or "+" ? Bullet : number.TrimEnd(')', '.') + ".");
                continue;
            }

            var content = trimmed.StartsWith('>') ? trimmed.TrimStart('>').Trim() : trimmed;
            if (pending.Length > 0)
            {
                pending.Append(' ').Append(content); // a wrapped line of the paragraph or list item above
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
                listIndents.Clear(); // an indented paragraph belongs to the list item above it
            Start(MarkdownBlockKind.Paragraph, content);
        }

        Flush();
        return blocks;
    }

    /// <summary>The inline runs of one block's text, adjacent runs of the same style merged.</summary>
    public static IReadOnlyList<MarkdownRun> ParseInline(string text)
    {
        var runs = new List<MarkdownRun>();
        Inline(text, MarkdownStyle.None, null, runs);
        return runs;
    }

    private static void Inline(string s, MarkdownStyle style, Uri? link, List<MarkdownRun> runs)
    {
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length > 0)
                Add(runs, plain.ToString(), style, link);
            plain.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && char.IsAscii(s[i + 1]) && (char.IsPunctuation(s[i + 1]) || char.IsSymbol(s[i + 1])))
            {
                plain.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var ticks = Run(s, i, '`');
                var delimiter = new string('`', ticks);
                var close = s.IndexOf(delimiter, i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Flush();
                    var code = s[(i + ticks)..close];
                    if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ')
                        code = code[1..^1];
                    Add(runs, code, style | MarkdownStyle.Code, link);
                    i = close + ticks;
                    continue;
                }

                plain.Append(delimiter);
                i += ticks;
                continue;
            }

            if (c is '*' or '_')
            {
                var n = Math.Min(Run(s, i, c), 2);
                var close = CanOpen(s, i, n) ? FindClose(s, i + n, c, n) : -1;
                if (close > 0)
                {
                    Flush();
                    Inline(s[(i + n)..close], style | (n == 2 ? MarkdownStyle.Bold : MarkdownStyle.Italic), link, runs);
                    i = close + n;
                    continue;
                }

                plain.Append(c, n);
                i += n;
                continue;
            }

            if (link is null && (c == '[' || (c == '!' && i + 1 < s.Length && s[i + 1] == '[')))
            {
                var open = c == '!' ? i + 1 : i;
                if (TryLink(s, open, out var label, out var target, out var end))
                {
                    Flush();
                    Inline(label, style, c == '!' ? null : target, runs); // an image shows its description
                    i = end;
                    continue;
                }
            }

            if (link is null && c == '<' && s.IndexOf('>', i + 1) is var gt and > 0 && WebAddress(s[(i + 1)..gt]) is { } angled)
            {
                Flush();
                Add(runs, s[(i + 1)..gt], style, angled);
                i = gt + 1;
                continue;
            }

            if (link is null && (c is 'h' or 'H') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && BareAddress(s, i) is { } bare)
            {
                Flush();
                Add(runs, bare.Text, style, bare.Uri);
                i += bare.Text.Length;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    private static void Add(List<MarkdownRun> runs, string text, MarkdownStyle style, Uri? link)
    {
        if (text.Length == 0)
            return;
        if (runs.Count > 0 && runs[^1] is var last && last.Style == style && last.Link == link)
            runs[^1] = last with { Text = last.Text + text };
        else
            runs.Add(new MarkdownRun(text, style, link));
    }

    private static int Run(string s, int i, char c)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == c)
            n++;
        return n;
    }

    /// <summary>Emphasis opens before a non-space; <c>_</c> also not inside a word (snake_case stays as is).</summary>
    private static bool CanOpen(string s, int i, int n) =>
        i + n < s.Length && !char.IsWhiteSpace(s[i + n]) && (s[i] != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]));

    private static int FindClose(string s, int from, char c, int n)
    {
        for (var j = from + 1; j + n <= s.Length; j++)
        {
            if (s[j] == '`')
            {
                var ticks = Run(s, j, '`');
                var end = s.IndexOf(new string('`', ticks), j + ticks, StringComparison.Ordinal);
                if (end > 0)
                    j = end + ticks - 1; // emphasis doesn't close inside a code span
                continue;
            }

            if (s[j] != c || char.IsWhiteSpace(s[j - 1]))
                continue;
            var run = Run(s, j, c);
            if (run < n)
                continue;
            if (n == 1 && run > 1)
            {
                j++; // a ** inside *italic* belongs to bold
                continue;
            }

            if (c == '_' && j + run < s.Length && char.IsLetterOrDigit(s[j + run]))
                continue;
            return j + run - n; // ***both***: bold closes on the last two, inside it italic on the first
        }

        return -1;
    }

    /// <summary><c>[label](address "title")</c> starting at <paramref name="open"/>; the target is null unless it's an absolute http(s) address.</summary>
    private static bool TryLink(string s, int open, out string label, out Uri? target, out int end)
    {
        (label, target, end) = ("", null, 0);
        var close = Matching(s, open, '[', ']');
        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
            return false;
        var paren = Matching(s, close + 1, '(', ')');
        if (paren < 0)
            return false;
        var address = s[(close + 2)..paren].Trim();
        if (address.StartsWith('<') && address.IndexOf('>') is var gt and > 0)
            address = address[1..gt];
        else if (address.IndexOfAny([' ', '\t']) is var space and > 0)
            address = address[..space]; // drop the title
        (label, target, end) = (s[(open + 1)..close], WebAddress(address), paren + 1);
        return true;
    }

    private static int Matching(string s, int open, char opening, char closing)
    {
        var depth = 0;
        for (var j = open; j < s.Length; j++)
        {
            if (s[j] == '\\')
                j++;
            else if (s[j] == opening)
                depth++;
            else if (s[j] == closing && --depth == 0)
                return j;
        }

        return -1;
    }

    /// <summary>A bare http(s) address at <paramref name="i"/>, without the punctuation that ends a sentence around it.</summary>
    private static (string Text, Uri Uri)? BareAddress(string s, int i)
    {
        var end = i;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<')
            end++;
        var text = s[i..end].TrimEnd('.', ',', ':', ';', '!', '?', '\'', '"', '*', '_');
        if (text.EndsWith(')') && !text.Contains('(', StringComparison.Ordinal))
            text = text[..^1];
        return WebAddress(text) is { } uri ? (text, uri) : null;
    }

    private static Uri? WebAddress(string text) =>
        (text.StartsWith(Uri.UriSchemeHttps + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(Uri.UriSchemeHttp + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase))
        && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri
            : null;

    private static int Indent(string whitespace) => whitespace.Sum(c => c == '\t' ? 4 : 1);

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}([-*_])([ \t]*\1){2,}[ \t]*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:[ \t]*\S")]
    internal static partial Regex LinkDefinition();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();
}
