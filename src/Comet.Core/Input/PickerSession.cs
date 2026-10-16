namespace Comet.Core.Input;

/// <summary>What the host should do after a key went through the picker.</summary>
public enum PickerAction : byte
{
    None,
    /// <summary>The token changed: search again and show/update/hide the popup.</summary>
    QueryChanged,
    /// <summary>The session ended without inserting anything: hide the popup.</summary>
    Cancel,
    SelectNext,
    SelectPrevious,
    /// <summary>Insert the selected result in place of the token (Enter, Tab or a click).</summary>
    CommitSelected,
    /// <summary>Insert the exact match for the token (closing colon); the closing colon was swallowed.</summary>
    CommitExact,
    /// <summary>Insert the exact match for the token, then re-type the swallowed space.</summary>
    CommitExactThenSpace,
}

/// <param name="Swallow">True when the key must not reach the target app.</param>
/// <param name="DeleteCount">For commits: characters to erase before inserting (trigger + token).</param>
public readonly record struct PickerDecision(bool Swallow, PickerAction Action, int DeleteCount = 0);

/// <summary>Resolves whether a token names something exactly (a shortcode, alias or snippet name).</summary>
public interface IExactMatcher
{
    bool HasExact(ReadOnlySpan<char> token);
}

/// <summary>
/// The <c>:</c> trigger state machine. It sees every translated key, decides synchronously whether the
/// key must be swallowed, and tells the host what to do. No allocations; not thread-safe (hook thread only).
/// <para>Trigger rules: <c>:</c> starts a session only after start-of-text, whitespace, opening punctuation or
/// an inserted emoji, so <c>10:30</c>, <c>https:</c> and <c>C:\</c> never trigger. A token starting with an
/// uppercase letter is an emoticon (<c>:D</c>, <c>:P</c>); <c>+</c>/<c>-</c> must be followed by a digit
/// (<c>:+1</c>, but not <c>:-)</c>).</para>
/// </summary>
public sealed class PickerSession
{
    public const int MaxTokenLength = 32;

    private readonly char[] _token = new char[MaxTokenLength];
    private int _length;
    private int _committedLength;
    private char _previous = Unknown;

    // Sentinels for "what came before the caret": unknown (after a reset) and an inserted emoji/snippet.
    private const char Unknown = '\uFFFF';
    private const char Inserted = '\uFFFE';

    public char TriggerChar { get; set; } = ':';

    /// <summary>Characters needed after the trigger before the popup shows (≥ 1).</summary>
    public int MinChars { get; set; } = 2;

    public IExactMatcher? ExactMatcher { get; set; }

    /// <summary>True between a valid trigger and the end of the session.</summary>
    public bool IsActive { get; private set; }

    public ReadOnlySpan<char> Token => _token.AsSpan(0, _length);

    /// <summary>The token of the session that just ended with a commit (for resolving <c>:code:</c>).</summary>
    public ReadOnlySpan<char> CommittedToken => _token.AsSpan(0, _committedLength);

    /// <summary>True once the token is long enough for the popup.</summary>
    public bool HasQuery => IsActive && _length >= MinChars;

    /// <summary>Ends any session and forgets the text context (focus change, click, caret moved elsewhere).</summary>
    public void Reset()
    {
        IsActive = false;
        _length = 0;
        _previous = Unknown;
    }

    /// <summary>Called after the host inserted an emoji or snippet: a trigger may follow immediately (<c>:a::b:</c>).</summary>
    public void NotifyInserted()
    {
        IsActive = false;
        _length = 0;
        _previous = Inserted;
    }

    /// <param name="popupVisible">Whether the popup currently shows results (navigation keys are only swallowed then).</param>
    public PickerDecision OnKey(KeyInput key, bool popupVisible)
    {
        if (!IsActive)
        {
            if (key.Kind == KeyKind.Char && key.Char == TriggerChar && CanStartAfter(_previous))
            {
                IsActive = true;
                _length = 0;
                return new PickerDecision(false, PickerAction.QueryChanged);
            }

            _previous = After(_previous, key);
            return default;
        }

        switch (key.Kind)
        {
            case KeyKind.Char:
                return OnChar(key.Char);

            case KeyKind.Backspace:
                if (_length == 0)
                    return End(PickerAction.Cancel, swallow: false, previous: Unknown); // the trigger itself was erased
                _length--;
                return new PickerDecision(false, PickerAction.QueryChanged);

            case KeyKind.Enter:
            case KeyKind.Tab when !key.Shift:
                if (popupVisible)
                    return End(PickerAction.CommitSelected, swallow: true, previous: Inserted, delete: 1 + _length);
                return End(PickerAction.Cancel, swallow: false, previous: key.Kind == KeyKind.Enter ? '\n' : '\t');

            case KeyKind.Tab:
                return popupVisible ? new PickerDecision(true, PickerAction.SelectPrevious) : End(PickerAction.Cancel, false, '\t');

            case KeyKind.Up:
                return popupVisible ? new PickerDecision(true, PickerAction.SelectPrevious) : End(PickerAction.Cancel, false, Unknown);

            case KeyKind.Down:
                return popupVisible ? new PickerDecision(true, PickerAction.SelectNext) : End(PickerAction.Cancel, false, Unknown);

            case KeyKind.Escape:
                // Swallow Esc only when it closes our popup; otherwise the app should see it.
                return End(PickerAction.Cancel, swallow: popupVisible, previous: Unknown);

            case KeyKind.Ignored:
                return default;

            default: // navigation, shortcuts, dead keys, IME
                return End(PickerAction.Cancel, swallow: false, previous: Unknown);
        }
    }

    /// <summary>The character before the caret after <paramref name="key"/>, as far as we can tell.</summary>
    private static char After(char previous, KeyInput key) => key.Kind switch
    {
        KeyKind.Char => key.Char,
        KeyKind.Enter => '\n',
        KeyKind.Tab => '\t',
        KeyKind.Ignored or KeyKind.Escape => previous,
        _ => Unknown,
    };

    private static bool CanStartAfter(char previous) =>
        previous is Unknown or Inserted || TextClass.IsWhitespace(previous) || TextClass.IsOpeningPunctuation(previous);

    private PickerDecision OnChar(char c)
    {
        if (c == TriggerChar)
        {
            // Closing colon: replace ":code:" directly when the code names something exactly.
            if (_length > 0 && ExactMatcher?.HasExact(Token) == true)
                return End(PickerAction.CommitExact, swallow: true, previous: Inserted, delete: 1 + _length);
            return End(PickerAction.Cancel, swallow: false, previous: c);
        }

        if (c == ' ')
        {
            if (_length >= MinChars && ExactMatcher?.HasExact(Token) == true)
                return End(PickerAction.CommitExactThenSpace, swallow: true, previous: ' ', delete: 1 + _length);
            return End(PickerAction.Cancel, swallow: false, previous: ' ');
        }

        if (!IsTokenChar(c) || _length == MaxTokenLength || !ValidAt(_length, c))
            return End(PickerAction.Cancel, swallow: false, previous: c);

        _token[_length++] = c;
        return new PickerDecision(false, PickerAction.QueryChanged);
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '+';

    private bool ValidAt(int index, char c)
    {
        if (index == 0)
            return !char.IsUpper(c); // ":D", ":P", ":O" are emoticons
        if (index == 1 && _token[0] is '+' or '-')
            return char.IsAsciiDigit(c); // ":+1", ":-1" but not ":-)" or ":-P"
        return true;
    }

    private PickerDecision End(PickerAction action, bool swallow, char previous, int delete = 0)
    {
        _committedLength = action is PickerAction.CommitSelected or PickerAction.CommitExact or PickerAction.CommitExactThenSpace ? _length : 0;
        IsActive = false;
        _length = 0;
        _previous = previous;
        return new PickerDecision(swallow, action, delete);
    }
}
