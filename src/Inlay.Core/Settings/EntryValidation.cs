using Inlay.Core.Input;
using Inlay.Core.Snippets;
using Inlay.Core.Text;

namespace Inlay.Core.Settings;

public enum Severity
{
    Warning,
    Error,
}

/// <summary>The dialog field a message belongs to (it is shown under that field).</summary>
public enum ValidationField
{
    Name,
    Text,
    Shorthand,
    Expansion,
}

public readonly record struct ValidationMessage(Severity Severity, ValidationField Field, string Text);

/// <summary>Live validation for the Add/Edit dialogs. Errors block saving; warnings only inform.</summary>
public static class EntryValidation
{
    /// <param name="otherNames">Names of the other snippets (the one being edited excluded).</param>
    /// <param name="isEmojiShortcode">True when a name is also an emoji shortcode (warn about the collision).</param>
    /// <param name="trigger">The trigger character, for codes shown in messages.</param>
    public static IReadOnlyList<ValidationMessage> ValidateSnippet(string name, string text, IEnumerable<string> otherNames, Func<string, bool>? isEmojiShortcode, string trigger = ":")
    {
        var messages = new List<ValidationMessage>();
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Name, "Give the snippet a name."));
        else if (trimmed.Any(char.IsWhiteSpace))
            messages.Add(new(Severity.Error, ValidationField.Name, "Names can't contain spaces. Use - or _ instead."));
        else if (trimmed.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '+')))
            messages.Add(new(Severity.Error, ValidationField.Name, "Names can only contain letters, digits, - and _."));
        else if (otherNames.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            messages.Add(new(Severity.Error, ValidationField.Name, $"There's already a snippet named “{trimmed}”."));
        else if (isEmojiShortcode?.Invoke(trimmed) == true)
            messages.Add(new(Severity.Warning, ValidationField.Name, $"“{TriggerFormat.Code(trigger, trimmed)}” is also an emoji; the snippet will be picked instead."));

        if (text.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Text, "The snippet has no text."));
        foreach (var p in SnippetTemplate.Validate(text))
            messages.Add(new(Severity.Warning, ValidationField.Text, $"{p.Message} It will be inserted as typed."));
        return messages;
    }

    public static IReadOnlyList<ValidationMessage> ValidateShorthand(string abbreviation, string replacement, IEnumerable<string> otherAbbreviations)
    {
        var messages = new List<ValidationMessage>();
        var a = abbreviation.Trim();
        if (a.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Type the abbreviation."));
        else if (a.Any(char.IsWhiteSpace))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Abbreviations can't contain spaces."));
        else if (a.Any(c => TextClass.IsClosingPunctuation(c) || TextClass.IsOpeningPunctuation(c)))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Abbreviations can't contain punctuation that ends or starts a word, such as . , ; : ! ? ( ) or quotes."));
        else if (otherAbbreviations.Any(o => string.Equals(o.Trim(), a, StringComparison.OrdinalIgnoreCase)))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, $"“{a}” is already in the list."));
        else if (CommonWords.Contains(a))
            messages.Add(new(Severity.Warning, ValidationField.Shorthand, $"“{a}” is a common word, so it will expand very often."));

        if (replacement.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Expansion, "Type the replacement."));
        return messages;
    }

    public static bool HasErrors(IEnumerable<ValidationMessage> messages) => messages.Any(m => m.Severity == Severity.Error);

    /// <summary>Very common English and Italian words that would fire constantly as abbreviations.</summary>
    internal static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at", "by", "for", "with", "from", "up", "out",
        "as", "is", "it", "its", "be", "am", "are", "was", "were", "been", "do", "does", "did", "done", "have", "has", "had",
        "i", "me", "my", "we", "us", "our", "you", "your", "he", "him", "his", "she", "her", "they", "them", "their", "this",
        "that", "these", "those", "what", "who", "how", "why", "when", "where", "which", "not", "no", "yes", "so", "too",
        "all", "any", "can", "will", "would", "should", "could", "just", "now", "then", "than", "there", "here", "go",
        "get", "got", "see", "say", "one", "two", "new", "good", "ok", "okay", "hi", "hey", "bye", "thanks", "please",
        // Italian
        "il", "lo", "la", "i", "gli", "le", "un", "uno", "una", "di", "da", "in", "con", "su", "per", "tra", "fra", "e",
        "ed", "o", "od", "ma", "se", "che", "chi", "cui", "non", "si", "sì", "no", "ci", "vi", "ne", "mi", "ti", "lui",
        "lei", "noi", "voi", "loro", "mio", "tuo", "suo", "è", "sono", "sei", "siamo", "siete", "ho", "hai", "ha",
        "abbiamo", "avete", "hanno", "fa", "fai", "va", "vai", "del", "della", "dei", "delle", "al", "alla", "ai", "alle",
        "dal", "dalla", "nel", "nella", "sul", "sulla", "come", "dove", "quando", "perché", "anche", "più", "già", "poi",
        "qui", "qua", "là", "lì", "ora", "oggi", "ieri", "domani", "bene", "male", "molto", "poco", "tutto", "tutti",
        "ciao", "grazie", "prego", "si", "allora", "quindi", "però", "solo", "sempre", "mai", "ok",
    };
}
