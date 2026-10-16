using System.Globalization;
using Comet.Core.Input;
using Comet.Core.Text;

namespace Comet.Core.AutoReplace;

/// <summary>An abbreviation that expands by itself.</summary>
public sealed record AutoReplaceRule(string Abbreviation, string Replacement, bool MatchCase = false);

/// <summary>The key that ended the word, re-sent after the replacement.</summary>
public enum BoundaryKind : byte
{
    Char,
    Enter,
    Tab,
}

public readonly record struct Boundary(BoundaryKind Kind, char Char = '\0');

public enum AutoReplaceAction : byte
{
    None,
    /// <summary>Erase <see cref="AutoReplaceDecision.DeleteCount"/> characters, type <see cref="AutoReplaceDecision.Text"/>, then re-send the boundary.</summary>
    Expand,
    /// <summary>Backspace right after an expansion: erase the expansion and boundary, type the original word, then re-send the boundary.</summary>
    Undo,
}

public readonly record struct AutoReplaceDecision(bool Swallow, AutoReplaceAction Action, int DeleteCount = 0, string? Text = null, Boundary Boundary = default);

/// <summary>
/// Tracks the word being typed in a fixed buffer and expands abbreviations at word boundaries.
/// <list type="bullet">
/// <item>Fires only on a whole word: the word must start after whitespace, opening punctuation or a reset,
/// never inside a word or after a letter or digit.</item>
/// <item>Case follows the typed word (<c>cmq</c>/<c>Cmq</c>/<c>CMQ</c>) unless the rule matches case exactly.</item>
/// <item>Backspace right after an expansion restores exactly what was typed, and that word does not
/// expand again until something else is typed.</item>
/// </list>
/// Lookups use span-keyed dictionaries, so tracking and matching allocate nothing.
/// </summary>
public sealed class AutoReplaceEngine
{
    private const int MaxWord = 48;

    private readonly char[] _word = new char[MaxWord];
    private int _length;
    private bool _valid = true;

    private Dictionary<string, AutoReplaceRule> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AutoReplaceRule> _exactCase = new(StringComparer.Ordinal);
    private Dictionary<string, AutoReplaceRule>.AlternateLookup<ReadOnlySpan<char>> _ignoreCaseLookup;
    private Dictionary<string, AutoReplaceRule>.AlternateLookup<ReadOnlySpan<char>> _exactCaseLookup;

    // Last expansion, for undo. Cleared by any key other than the undo Backspace.
    private string? _undoWord;
    private string? _undoText;
    private Boundary _undoBoundary;

    // After an undo, this word stays unexpanded until something else is typed.
    private string? _suppressed;

    public AutoReplaceEngine() => SetRules([]);

    /// <summary>Number of active rules.</summary>
    public int RuleCount => _ignoreCase.Count + _exactCase.Count;

    /// <summary>Replaces the rule set (called when the user edits the list; not on the hot path).</summary>
    public void SetRules(IEnumerable<AutoReplaceRule> rules)
    {
        var ignoreCase = new Dictionary<string, AutoReplaceRule>(StringComparer.OrdinalIgnoreCase);
        var exactCase = new Dictionary<string, AutoReplaceRule>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (string.IsNullOrEmpty(r.Abbreviation) || r.Abbreviation.Length > MaxWord)
                continue;
            if (r.MatchCase)
                exactCase.TryAdd(r.Abbreviation, r);
            else
                ignoreCase.TryAdd(r.Abbreviation, r);
        }

        _ignoreCase = ignoreCase;
        _exactCase = exactCase;
        _ignoreCaseLookup = ignoreCase.GetAlternateLookup<ReadOnlySpan<char>>();
        _exactCaseLookup = exactCase.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The word typed so far (for tests and diagnostics).</summary>
    public ReadOnlySpan<char> CurrentWord => _word.AsSpan(0, _length);

    /// <summary>True when the current word could still become an abbreviation (used to prefetch the focus context).</summary>
    public bool IsCandidate => _valid && _length > 0 && RuleCount > 0;

    /// <summary>Forgets the word (focus change, click, caret moved).</summary>
    public void Reset()
    {
        _length = 0;
        _valid = true;
        _undoWord = null;
        _undoText = null;
        _suppressed = null;
    }

    /// <summary>Called when something else (the picker) inserted text: the next word starts fresh.</summary>
    public void NotifyInserted() => Reset();

    /// <param name="allowed">False in password fields, elevated or excluded apps: track nothing, expand nothing.</param>
    public AutoReplaceDecision OnKey(KeyInput key, bool allowed = true)
    {
        // Undo: Backspace immediately after an expansion.
        if (_undoWord is not null)
        {
            var word = _undoWord;
            var text = _undoText!;
            var boundary = _undoBoundary;
            _undoWord = _undoText = null;
            if (key.Kind == KeyKind.Backspace && allowed)
            {
                _suppressed = word;
                ClearWord();
                return new AutoReplaceDecision(true, AutoReplaceAction.Undo, SearchText.GraphemeCount(text) + 1, word, boundary);
            }
        }

        switch (key.Kind)
        {
            case KeyKind.Char when key.Char == ' ' || TextClass.IsWhitespace(key.Char):
                return OnBoundary(new Boundary(BoundaryKind.Char, key.Char), allowed);
            case KeyKind.Char when TextClass.IsClosingPunctuation(key.Char) && _length > 0:
                return OnBoundary(new Boundary(BoundaryKind.Char, key.Char), allowed);
            case KeyKind.Char when TextClass.IsOpeningPunctuation(key.Char) || TextClass.IsClosingPunctuation(key.Char):
                ClearWord();
                return default;
            case KeyKind.Char:
                Append(key.Char);
                return default;
            case KeyKind.Enter:
                return OnBoundary(new Boundary(BoundaryKind.Enter), allowed);
            case KeyKind.Tab:
                return OnBoundary(new Boundary(BoundaryKind.Tab), allowed);
            case KeyKind.Backspace:
                if (_length > 0)
                    _length--;
                else
                    _valid = false; // erasing into text we never saw
                return default;
            case KeyKind.Ignored:
            case KeyKind.DeadKey: // the composed character arrives with the next key
                return default;
            default:
                // Caret moved, shortcut or IME: forget the word. The next word is assumed to start fresh.
                Reset();
                return default;
        }
    }

    private void Append(char c)
    {
        _suppressed = null;
        if (_length == MaxWord)
        {
            _valid = false;
            return;
        }

        _word[_length++] = c;
    }

    private void ClearWord()
    {
        _length = 0;
        _valid = true;
    }

    private AutoReplaceDecision OnBoundary(Boundary boundary, bool allowed)
    {
        var word = CurrentWord;
        var result = default(AutoReplaceDecision);
        if (allowed && _valid && _length > 0 && !(_suppressed is not null && word.SequenceEqual(_suppressed)) && TryMatch(word, out var replacement))
        {
            _undoWord = new string(word);
            _undoText = replacement;
            _undoBoundary = boundary;
            result = new AutoReplaceDecision(true, AutoReplaceAction.Expand, SearchText.GraphemeCount(word), replacement, boundary);
        }

        _suppressed = null;
        ClearWord();
        return result;
    }

    private bool TryMatch(ReadOnlySpan<char> word, out string replacement)
    {
        if (_exactCaseLookup.TryGetValue(word, out var exact))
        {
            replacement = exact.Replacement;
            return true;
        }

        if (_ignoreCaseLookup.TryGetValue(word, out var rule))
        {
            replacement = ApplyCase(word, rule.Replacement);
            return true;
        }

        replacement = "";
        return false;
    }

    /// <summary><c>cmq</c> → as defined, <c>Cmq</c> → Capitalized, <c>CMQ</c> → UPPERCASE.</summary>
    internal static string ApplyCase(ReadOnlySpan<char> typed, string replacement)
    {
        var letters = 0;
        var upper = 0;
        foreach (var c in typed)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (char.IsUpper(c))
                upper++;
        }

        if (letters == 0 || upper == 0 || replacement.Length == 0)
            return replacement;
        var culture = CultureInfo.CurrentCulture;
        if (upper == letters && letters >= 2)
            return replacement.ToUpper(culture);
        if (char.IsUpper(FirstLetter(typed)))
        {
            var i = replacement.AsSpan().IndexOfAnyExcept(" \t\r\n");
            if (i < 0)
                return replacement;
            return string.Concat(replacement.AsSpan(0, i), char.ToUpper(replacement[i], culture).ToString(), replacement.AsSpan(i + 1));
        }

        return replacement;
    }

    private static char FirstLetter(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (char.IsLetter(c))
                return c;
        }

        return '\0';
    }
}
