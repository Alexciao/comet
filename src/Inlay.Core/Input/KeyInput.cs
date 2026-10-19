namespace Inlay.Core.Input;

/// <summary>What a physical key press means once translated with the foreground window's keyboard layout.</summary>
public enum KeyKind : byte
{
    /// <summary>Produced a character (<see cref="KeyInput.Char"/>), including space.</summary>
    Char,
    Backspace,
    Enter,
    Tab,
    Escape,
    Up,
    Down,
    /// <summary>Left, Right, Home, End, Page Up/Down, Delete: the caret moves or text changes outside our view.</summary>
    Navigation,
    /// <summary>A Ctrl/Alt/Win combination (shortcut), not text input.</summary>
    Shortcut,
    /// <summary>A dead key (accent waiting for the next key).</summary>
    DeadKey,
    /// <summary>The IME is composing (VK_PROCESSKEY).</summary>
    Ime,
    /// <summary>Keys that neither type nor move the caret (F-keys, modifiers alone, media keys).</summary>
    Ignored,
}

public readonly record struct KeyInput(KeyKind Kind, char Char = '\0', bool Shift = false)
{
    public static KeyInput Text(char c) => new(KeyKind.Char, c);

    public static KeyInput Of(KeyKind kind, bool shift = false) => new(kind, '\0', shift);
}

/// <summary>Character classes shared by the picker trigger and shorthands word tracking.</summary>
public static class TextClass
{
    /// <summary>Characters after which a new word (or a <c>:</c> trigger) may start.</summary>
    public static bool IsOpeningPunctuation(char c) => c is '(' or '[' or '{' or '"' or '\'' or '«' or '“' or '‘' or '¿' or '¡' or '<' or '„';

    /// <summary>Punctuation that ends a word for shorthands (besides whitespace).</summary>
    public static bool IsClosingPunctuation(char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '»' or '”' or '…';

    public static bool IsWhitespace(char c) => char.IsWhiteSpace(c);
}
