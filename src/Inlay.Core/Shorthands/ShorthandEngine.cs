using System.Globalization;
using Inlay.Core.Input;
using Inlay.Core.Text;

namespace Inlay.Core.Shorthands;

/// <summary>An abbreviation that expands by itself.</summary>
public sealed record ShorthandRule(string Shorthand, string Expansion, bool MatchCase = false);

/// <summary>The key that ended the word, re-sent after the replacement.</summary>
public enum BoundaryKind : byte
{
    Char,
    Enter,
    Tab,
}

/// <param name="Shift">Enter or Tab was pressed with Shift (a new line in chat apps, backwards in forms): re-sent the same way.</param>
public readonly record struct Boundary(BoundaryKind Kind, char Char = '\0', bool Shift = false);

public enum ShorthandAction : byte
{
    None,
    /// <summary>Erase <see cref="ShorthandDecision.DeleteCount"/> characters, type <see cref="ShorthandDecision.Text"/>, then re-send the boundary.</summary>
    Expand,
    /// <summary>Backspace right after an expansion: erase the expansion and boundary, type the original word, then re-send the boundary.</summary>
    Undo,
}

/// <param name="DeleteCount">Characters (graphemes) to erase before typing.</param>
/// <param name="Deleted">The exact text those characters are, for editors that need UTF-16 positions.</param>
public readonly record struct ShorthandDecision(bool Swallow, ShorthandAction Action, int DeleteCount = 0, string? Text = null, Boundary Boundary = default, string? Deleted = null);

/// <summary>
/// Expands abbreviations at word boundaries, from a short history of what was typed since the last reset.
/// <list type="bullet">
/// <item>Fires only on a whole word: after whitespace or opening punctuation, or at the start of what Inlay saw
/// (after a click or focus change). Never inside a word, right after closing punctuation (<c>inlay.it</c>,
/// <c>3.5</c>) or after an apostrophe inside a word (<c>you're</c>).</item>
/// <item>Case follows the typed word (<c>cmq</c>/<c>Cmq</c>/<c>CMQ</c>) unless the rule matches case exactly; a
/// word typed exactly as the abbreviation is written gives the replacement exactly as written.</item>
/// <item>Backspace right after an expansion restores exactly what was typed, and that word does not expand
/// again until something else is typed. Not after Enter or Tab: they may already have sent a message or moved
/// focus, and undoing would press them again.</item>
/// <item>Backspace edits the history, so erasing back into a word continues that word.</item>
/// </list>
/// </summary>
public sealed class ShorthandEngine
{
    private const int MaxWord = 48;
    private const int MaxHistory = 256;

    // What was typed since the last reset, as the field should now contain it (up to the caret).
    private readonly char[] _history = new char[MaxHistory];
    private int _count;

    // True when the start of the history is a word start: right after a reset (DECISIONS: unknown text before the
    // caret counts as a word start). False once the history was trimmed (it then starts mid-text).
    private bool _startIsWordStart = true;

    private Dictionary<string, ShorthandRule> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ShorthandRule> _exactCase = new(StringComparer.Ordinal);
    private Dictionary<string, ShorthandRule>.AlternateLookup<ReadOnlySpan<char>> _ignoreCaseLookup;
    private Dictionary<string, ShorthandRule>.AlternateLookup<ReadOnlySpan<char>> _exactCaseLookup;

    // Last expansion, for undo. Cleared by any key other than the undo Backspace.
    private string? _undoWord;
    private string? _undoText;
    private Boundary _undoBoundary;

    // After an undo, this word stays unexpanded until something else is typed.
    private string? _suppressed;

    // A dead key was typed and the key resolving it is arriving (see OnKey); _deadKeyAccent is its accent until seen.
    private bool _resolvingDeadKey;
    private char _deadKeyAccent;

    public ShorthandEngine() => SetRules([]);

    /// <summary>Number of active rules.</summary>
    public int RuleCount => _ignoreCase.Count + _exactCase.Count;

    /// <summary>Replaces the rule set (called when the user edits the list; not on the hot path).</summary>
    public void SetRules(IEnumerable<ShorthandRule> rules)
    {
        var ignoreCase = new Dictionary<string, ShorthandRule>(StringComparer.OrdinalIgnoreCase);
        var exactCase = new Dictionary<string, ShorthandRule>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (string.IsNullOrEmpty(r.Shorthand) || r.Shorthand.Length > MaxWord)
                continue;
            if (r.MatchCase)
                exactCase.TryAdd(r.Shorthand, r);
            else
                ignoreCase.TryAdd(r.Shorthand, r);
        }

        _ignoreCase = ignoreCase;
        _exactCase = exactCase;
        _ignoreCaseLookup = ignoreCase.GetAlternateLookup<ReadOnlySpan<char>>();
        _exactCaseLookup = exactCase.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The word typed so far (for tests and diagnostics).</summary>
    public ReadOnlySpan<char> CurrentWord => _history.AsSpan(WordStart(), _count - WordStart());

    /// <summary>True when the current word could still become an abbreviation (used to prefetch the focus context).</summary>
    public bool IsCandidate => RuleCount > 0 && _count > 0 && CanExpandAt(WordStart()) && _count - WordStart() is > 0 and <= MaxWord;

    /// <summary>
    /// True while Inlay knows something about the text before the caret (a word, an undo it could offer). A click
    /// then moves the caret away from it, so the caller should watch for clicks.
    /// </summary>
    public bool HasState => _count > 0 || _undoWord is not null;

    /// <summary>Forgets everything (focus change, click, caret moved): the text before the caret is unknown.</summary>
    public void Reset()
    {
        _count = 0;
        _startIsWordStart = true;
        _undoWord = null;
        _undoText = null;
        _suppressed = null;
    }

    /// <summary>Called when something else (the picker) inserted text: the next word starts fresh.</summary>
    public void NotifyInserted() => Reset();

    /// <param name="allowed">False in password fields, elevated or excluded apps: track nothing, expand nothing.</param>
    public ShorthandDecision OnKey(KeyInput key, bool allowed = true)
    {
        // The key resolving a dead key must reach the app, which holds the accent: swallowing it to expand would leave
        // the accent pending there, to land on the replacement, and lose the key's own character. So it never expands.
        // It gives the composed character (the accent alone before a space), or the accent uncombined followed by
        // its own character or action: once the accent arrives, the input after it is still that key's. (After a dead
        // key and space, that holds back the next key too, which only matters to a word ending in the accent.)
        // Modifiers and keys that type nothing leave the accent pending.
        if (_resolvingDeadKey && key.Kind is not (KeyKind.DeadKey or KeyKind.Ignored))
        {
            allowed = false;
            if (key.Kind == KeyKind.Char && key.Char == _deadKeyAccent && key.Char != '\0')
                _deadKeyAccent = '\0';
            else
                _resolvingDeadKey = false;
        }

        // Undo: Backspace immediately after an expansion.
        if (_undoWord is not null)
        {
            var word = _undoWord;
            var text = _undoText!;
            var boundary = _undoBoundary;
            _undoWord = _undoText = null;
            if (key.Kind == KeyKind.Backspace && allowed)
            {
                // The original word and boundary are typed back; erasing that boundary again brings the word back.
                _suppressed = word;
                Remove(text.Length + 1);
                Append(word);
                Append(boundary.Char);
                return new ShorthandDecision(true, ShorthandAction.Undo, SearchText.GraphemeCount(text) + 1, word, boundary, text + boundary.Char);
            }
        }

        switch (key.Kind)
        {
            case KeyKind.Char when TextClass.IsWhitespace(key.Char):
                return OnBoundary(new Boundary(BoundaryKind.Char, key.Char), allowed);
            case KeyKind.Char when TextClass.IsClosingPunctuation(key.Char) && _count > 0 && !IsSeparator(_history[_count - 1]):
                return OnBoundary(new Boundary(BoundaryKind.Char, key.Char), allowed);
            case KeyKind.Char:
                _suppressed = null;
                Append(key.Char);
                return default;
            case KeyKind.Enter:
                return OnBoundary(new Boundary(BoundaryKind.Enter, '\n', key.Shift), allowed);
            case KeyKind.Tab:
                return OnBoundary(new Boundary(BoundaryKind.Tab, '\t', key.Shift), allowed);
            case KeyKind.Backspace:
                // Past the start of the history the text is unknown, as after a click: the next word still counts as
                // a fresh word (select all, Backspace, then an abbreviation is the common case).
                if (_count > 0)
                    RemoveGrapheme();
                return default;
            case KeyKind.DeadKey: // the composed character arrives with the next key
                _resolvingDeadKey = true;
                _deadKeyAccent = key.Char;
                return default;
            case KeyKind.Ignored:
                return default;
            default:
                // Caret moved, shortcut, paste or IME: forget the text. The next word is assumed to start fresh.
                Reset();
                return default;
        }
    }

    private ShorthandDecision OnBoundary(Boundary boundary, bool allowed)
    {
        var start = WordStart();
        var word = _history.AsSpan(start, _count - start);
        var result = default(ShorthandDecision);
        if (allowed && word.Length is > 0 and <= MaxWord && CanExpandAt(start)
            && !(_suppressed is not null && word.SequenceEqual(_suppressed)) && TryMatch(word, out var replacement))
        {
            var typed = new string(word);
            result = new ShorthandDecision(true, ShorthandAction.Expand, SearchText.GraphemeCount(word), replacement, boundary, typed);
            if (boundary.Kind == BoundaryKind.Char)
            {
                _undoWord = typed;
                _undoText = replacement;
                _undoBoundary = boundary;
            }

            Remove(word.Length);
            Append(replacement);
        }

        _suppressed = null;
        if (boundary.Kind == BoundaryKind.Char)
            Append(boundary.Char);
        else
            Reset(); // Enter may have sent the message, Tab moved to another field: what follows is unknown
        return result;
    }

    /// <summary>Where the current word starts in the history: after the last separator.</summary>
    private int WordStart()
    {
        var i = _count;
        while (i > 0 && !IsSeparator(_history[i - 1], i - 1))
            i--;
        return i;
    }

    /// <summary>
    /// A word starting at <paramref name="start"/> may expand when what comes before it is whitespace or opening
    /// punctuation, or the start of the history when that is a word start.
    /// </summary>
    private bool CanExpandAt(int start)
    {
        if (start == 0)
            return _startIsWordStart;
        var before = _history[start - 1];
        return TextClass.IsWhitespace(before) || (TextClass.IsOpeningPunctuation(before) && !IsApostropheInWord(start - 1));
    }

    /// <summary>
    /// Characters that end a word. An apostrophe right after a letter is part of the word (<c>you're</c>,
    /// <c>l'altro</c>), not the opening quote it would be before one.
    /// </summary>
    private bool IsSeparator(char c, int index) =>
        TextClass.IsWhitespace(c) || TextClass.IsClosingPunctuation(c)
        || (TextClass.IsOpeningPunctuation(c) && !IsApostropheInWord(index));

    private bool IsSeparator(char c) => IsSeparator(c, _count - 1);

    private bool IsApostropheInWord(int index) =>
        _history[index] is '\'' or '’' && index > 0 && char.IsLetterOrDigit(_history[index - 1]);

    private void Append(char c)
    {
        if (_count == MaxHistory)
        {
            // Keep the recent half; what came before is now unknown (and not a word start).
            Array.Copy(_history, MaxHistory / 2, _history, 0, MaxHistory / 2);
            _count = MaxHistory / 2;
            _startIsWordStart = false;
        }

        _history[_count++] = c;
    }

    private void Append(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            Append(c);
    }

    private void Remove(int units)
    {
        if (units > _count)
        {
            _count = 0;
            _startIsWordStart = false;
            return;
        }

        _count -= units;
    }

    /// <summary>Backspace erases one character as the user sees it: a surrogate pair, or a letter with its marks.</summary>
    private void RemoveGrapheme()
    {
        var text = _history.AsSpan(0, _count);
        int position = 0, last = 0;
        while (position < text.Length)
        {
            last = position;
            position += StringInfo.GetNextTextElementLength(text[position..]);
        }

        _count = last;
    }

    private bool TryMatch(ReadOnlySpan<char> word, out string replacement)
    {
        if (_exactCaseLookup.TryGetValue(word, out var exact))
        {
            replacement = exact.Expansion;
            return true;
        }

        if (_ignoreCaseLookup.TryGetValue(word, out var rule))
        {
            // Typed exactly as the abbreviation is written (IIRC for "IIRC"): the replacement exactly as written.
            replacement = word.SequenceEqual(rule.Shorthand) ? rule.Expansion : ApplyCase(word, rule.Expansion);
            return true;
        }

        replacement = "";
        return false;
    }

    /// <summary><c>cmq</c> → as defined, <c>Cmq</c> → Capitalized, <c>CMQ</c> → UPPERCASE (in the user's culture).</summary>
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
            // The first letter, wherever it is: «perché» → «Perché».
            var i = 0;
            while (i < replacement.Length && !char.IsLetter(replacement[i]))
                i++;
            if (i == replacement.Length)
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
