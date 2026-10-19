using Inlay.Core.Text;

namespace Inlay.Core.Emoji;

/// <summary>
/// The convention for built-in Quick Insert codes (emoji shortcodes and symbol names): lowercase <c>snake_case</c>,
/// i.e. <c>^[a-z0-9]+(_[a-z0-9]+)*$</c>, unique across the dataset once folded. Applies to shipped data only;
/// user snippet and image names are never held to it.
/// </summary>
public static class EmojiCode
{
    /// <summary>True when <paramref name="code"/> is lowercase snake_case: letters and digits in words joined by single underscores.</summary>
    public static bool IsValid(ReadOnlySpan<char> code)
    {
        if (code.IsEmpty || code[0] == '_' || code[^1] == '_')
            return false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '_' ? code[i - 1] == '_' : !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Rewrites an upstream shortcode as snake_case ("T-Rex" → "t_rex", "piñata" → "pinata"): letters are folded to
    /// ASCII, anything else separates words. Returns "" when nothing is left.
    /// </summary>
    public static string Normalize(string code)
    {
        Span<char> folded = stackalloc char[2];
        var sb = new System.Text.StringBuilder(code.Length);
        var pendingSeparator = false;
        foreach (var c in code)
        {
            var n = SearchText.Fold([c], folded);
            var word = n > 0 && !folded[..n].ContainsAnyExceptInRange('\0', '\x7F');
            if (!word)
            {
                pendingSeparator = sb.Length > 0;
                continue;
            }

            if (pendingSeparator)
                sb.Append('_');
            pendingSeparator = false;
            sb.Append(folded[..n]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Shortcodes that can't be snake_case but people type from memory (<c>+1</c>, <c>-1</c>): kept as search-only
    /// aliases, never displayed.
    /// </summary>
    public static bool IsSignedNumber(string code) => code.Length >= 2 && code[0] is '+' or '-' && !code.AsSpan(1).ContainsAnyExceptInRange('0', '9');
}
