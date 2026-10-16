using System.Globalization;
using System.Text;

namespace Comet.Core.Snippets;

/// <summary>Values a snippet expansion may read. Injected so tests control time, culture and clipboard.</summary>
public interface ISnippetEnvironment
{
    DateTime Now { get; }

    CultureInfo Culture { get; }

    /// <summary>Current clipboard text, or empty when the clipboard holds no text.</summary>
    string ClipboardText();
}

/// <summary>A placeholder that could not be understood; it is inserted literally and shown in Settings.</summary>
public readonly record struct PlaceholderProblem(int Index, int Length, string Text, string Message);

/// <param name="Text">Text to insert.</param>
/// <param name="CursorIndex">Where <c>{cursor}</c> was (index into <paramref name="Text"/>), or -1 to leave the caret at the end.</param>
public readonly record struct SnippetExpansion(string Text, int CursorIndex)
{
    /// <summary>Text after the cursor position (what the caret has to move back over).</summary>
    public ReadOnlySpan<char> AfterCursor => CursorIndex < 0 ? [] : Text.AsSpan(CursorIndex);
}

/// <summary>
/// Snippet placeholders:
/// <c>{date}</c>, <c>{time}</c>, <c>{date:FORMAT}</c>, <c>{date+7d}</c>/<c>{date-1d}</c>/<c>{date+2w}</c>/<c>{date+1m}</c>
/// (optionally with <c>:FORMAT</c>), <c>{weekday}</c>, <c>{clipboard}</c>, <c>{cursor}</c>, and <c>{{</c>/<c>}}</c> for braces.
/// Anything else in braces is inserted literally and reported as a problem. Not a scripting language.
/// </summary>
public static class SnippetTemplate
{
    /// <summary>Every placeholder with a one-line description, for the "Insert placeholder" menu.</summary>
    public static IReadOnlyList<(string Placeholder, string Description)> Catalog { get; } =
    [
        ("{date}", "Today's date (short format)"),
        ("{time}", "The current time (short format)"),
        ("{date:dddd d MMMM yyyy}", "Date or time in a custom format"),
        ("{date+7d}", "A date offset by days (d), weeks (w) or months (m)"),
        ("{date+1d:dd/MM}", "An offset date in a custom format"),
        ("{weekday}", "Today's weekday name"),
        ("{clipboard}", "The text on the clipboard"),
        ("{cursor}", "Where the caret goes after inserting"),
        ("{{", "A literal {"),
        ("}}", "A literal }"),
    ];

    /// <summary>Finds placeholders that will be inserted literally because they are unknown or malformed.</summary>
    public static IReadOnlyList<PlaceholderProblem> Validate(string template)
    {
        var problems = new List<PlaceholderProblem>();
        Walk(template, null, problems);
        return problems;
    }

    /// <summary>Expands placeholders for insertion right now.</summary>
    public static SnippetExpansion Expand(string template, ISnippetEnvironment env)
    {
        var sb = new StringBuilder(template.Length + 32);
        var cursor = Walk(template, (sb, env), null);
        return new SnippetExpansion(sb.ToString(), cursor);
    }

    /// <returns>Index of <c>{cursor}</c> in the output, or -1.</returns>
    private static int Walk(string t, (StringBuilder Output, ISnippetEnvironment Env)? expand, List<PlaceholderProblem>? problems)
    {
        var output = expand?.Output;
        var cursor = -1;
        var i = 0;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '{' && i + 1 < t.Length && t[i + 1] == '{')
            {
                output?.Append('{');
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < t.Length && t[i + 1] == '}')
            {
                output?.Append('}');
                i += 2;
                continue;
            }

            if (c != '{')
            {
                output?.Append(c);
                i++;
                continue;
            }

            var close = t.IndexOf('}', i + 1);
            var nextOpen = t.IndexOf('{', i + 1);
            if (close < 0 || (nextOpen >= 0 && nextOpen < close))
            {
                problems?.Add(new PlaceholderProblem(i, 1, "{", "Unclosed “{”. Write {{ for a literal brace."));
                output?.Append('{');
                i++;
                continue;
            }

            var body = t.AsSpan(i + 1, close - i - 1);
            var whole = t.Substring(i, close - i + 1);
            if (body.SequenceEqual("cursor"))
            {
                if (cursor >= 0)
                {
                    problems?.Add(new PlaceholderProblem(i, whole.Length, whole, "Only the first {cursor} is used."));
                    output?.Append(whole);
                }
                else
                {
                    cursor = output?.Length ?? 0;
                }
            }
            else if (TryEvaluate(body, expand?.Env, out var value, out var error))
            {
                output?.Append(value);
            }
            else
            {
                problems?.Add(new PlaceholderProblem(i, whole.Length, whole, error));
                output?.Append(whole);
            }

            i = close + 1;
        }

        return cursor;
    }

    private static bool TryEvaluate(ReadOnlySpan<char> body, ISnippetEnvironment? env, out string value, out string error)
    {
        value = "";
        error = "";
        var culture = env?.Culture ?? CultureInfo.CurrentCulture;
        var now = env?.Now ?? DateTime.Now;

        switch (body)
        {
            case "time":
                value = now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
                return true;
            case "weekday":
                value = now.ToString("dddd", culture);
                return true;
            case "clipboard":
                value = env?.ClipboardText() ?? "";
                return true;
        }

        if (!body.StartsWith("date"))
        {
            error = $"Unknown placeholder “{{{body}}}”.";
            return false;
        }

        var rest = body[4..];
        var date = now;
        if (!rest.IsEmpty && rest[0] is '+' or '-')
        {
            var colonAt = rest.IndexOf(':');
            var offset = colonAt < 0 ? rest : rest[..colonAt];
            if (!TryApplyOffset(offset, ref date))
            {
                error = $"Can't read the date offset “{offset}”. Use for example +7d, -1d, +2w or +1m.";
                return false;
            }

            rest = colonAt < 0 ? [] : rest[colonAt..];
        }

        if (rest.IsEmpty)
        {
            value = date.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
            return true;
        }

        if (rest[0] != ':' || rest.Length == 1)
        {
            error = $"Unknown placeholder “{{{body}}}”.";
            return false;
        }

        try
        {
            value = date.ToString(rest[1..].ToString(), culture);
            return true;
        }
        catch (FormatException)
        {
            error = $"“{rest[1..]}” is not a valid date format.";
            return false;
        }
    }

    private static bool TryApplyOffset(ReadOnlySpan<char> offset, ref DateTime date)
    {
        // Sign, 1–4 digits, unit.
        if (offset.Length < 3 || offset.Length > 6)
            return false;
        var sign = offset[0] == '-' ? -1 : 1;
        var unit = offset[^1];
        if (!int.TryParse(offset[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return false;
        n *= sign;
        switch (unit)
        {
            case 'd':
                date = date.AddDays(n);
                return true;
            case 'w':
                date = date.AddDays(7 * n);
                return true;
            case 'm':
                date = date.AddMonths(n);
                return true;
            default:
                return false;
        }
    }
}
