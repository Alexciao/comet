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
    /// <summary>Images: the chosen file.</summary>
    File,
    /// <summary>Folders and collections: the chosen folder.</summary>
    Folder,
    /// <summary>Collections: the prefix.</summary>
    Prefix,
}

public readonly record struct ValidationMessage(Severity Severity, ValidationField Field, string Text);

/// <summary>Live validation for the Add/Edit dialogs. Errors block saving; warnings only inform.</summary>
public static class EntryValidation
{
    /// <summary>Above this, many chat apps reject the upload (Discord's free limit is 25 MB; Slack and Teams resize or refuse).</summary>
    public const long LargeImageBytes = 25L * 1024 * 1024;

    /// <param name="otherNames">Names of the other snippets (the one being edited excluded).</param>
    /// <param name="isEmojiShortcode">True when a name is also an emoji shortcode (warn about the collision).</param>
    /// <param name="trigger">The trigger character, for codes shown in messages.</param>
    public static IReadOnlyList<ValidationMessage> ValidateSnippet(string name, string text, IEnumerable<string> otherNames, Func<string, bool>? isEmojiShortcode, string trigger = ":") =>
        ValidateSnippet(name, text, otherNames, [], isEmojiShortcode, trigger);

    /// <param name="otherNames">Names of the other snippets (the one being edited excluded).</param>
    /// <param name="imageNames">
    /// Every name Quick Insert knows an image by: images, folders and each collection item (see
    /// <c>ImagesService.Names</c>). Names are unique across snippets and images.
    /// </param>
    /// <param name="isEmojiShortcode">True when a name is also an emoji shortcode (warn about the collision).</param>
    /// <param name="trigger">The trigger character, for codes shown in messages.</param>
    public static IReadOnlyList<ValidationMessage> ValidateSnippet(string name, string text, IEnumerable<string> otherNames, IEnumerable<string> imageNames, Func<string, bool>? isEmojiShortcode, string trigger = ":")
    {
        var messages = new List<ValidationMessage>();
        if (NameProblem(name, "snippet", otherNames, imageNames, isEmojiShortcode, trigger) is { } problem)
            messages.Add(problem);

        if (text.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Text, "Type the text the snippet inserts"));
        foreach (var p in SnippetTemplate.Validate(text))
            messages.Add(new(Severity.Warning, ValidationField.Text, $"{p.Message} It will be inserted as typed."));
        return messages;
    }

    /// <summary>The Add/Edit image dialog: an image, a folder or a collection.</summary>
    /// <param name="snippetNames">Names of every snippet.</param>
    /// <param name="otherImageNames">Every name Quick Insert knows an image by, the entry being edited (and its collection items) excluded.</param>
    /// <param name="isEmojiShortcode">True when a name is also an emoji shortcode (warn: the image wins).</param>
    /// <param name="fileSize">Images: the file's size in bytes, when known (large files get a warning).</param>
    /// <param name="trigger">The trigger character, for codes shown in messages.</param>
    public static IReadOnlyList<ValidationMessage> ValidateImage(ImageEntry entry, IEnumerable<string> snippetNames, IEnumerable<string> otherImageNames, Func<string, bool>? isEmojiShortcode, long? fileSize = null, string trigger = ":")
    {
        var messages = new List<ValidationMessage>();
        if (entry.Kind == ImageEntryKind.Collection)
        {
            // The prefix may be empty; collection items that collide with other names are numbered, not refused.
            var prefix = entry.Prefix.Trim();
            if (prefix.Any(char.IsWhiteSpace))
                messages.Add(new(Severity.Error, ValidationField.Prefix, "The prefix can't contain spaces. Use - or _ instead."));
            else if (!prefix.All(IsNameChar))
                messages.Add(new(Severity.Error, ValidationField.Prefix, "The prefix can only contain letters, digits, - and _."));
        }
        else if (NameProblem(entry.Name, "image", snippetNames, otherImageNames, isEmojiShortcode, trigger) is { } problem)
        {
            messages.Add(problem);
        }

        if (entry.Kind == ImageEntryKind.Image)
        {
            if (string.IsNullOrWhiteSpace(entry.File))
                messages.Add(new(Severity.Error, ValidationField.File, "Choose an image."));
            else if (fileSize > LargeImageBytes)
                messages.Add(new(Severity.Warning, ValidationField.File, $"This image is {fileSize.Value / (1024 * 1024)} MB. Many chat apps reject files over 25 MB"));
        }
        else if (string.IsNullOrWhiteSpace(entry.Folder))
        {
            messages.Add(new(Severity.Error, ValidationField.Folder, "Choose a folder."));
        }

        return messages;
    }

    /// <summary>Letters, digits, <c>-</c>, <c>_</c> and <c>+</c> (as in <c>:+1</c>): what can follow the trigger.</summary>
    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '+';

    /// <summary>The first problem with a snippet or image name, or null. <paramref name="what"/> is "snippet" or "image".</summary>
    private static ValidationMessage? NameProblem(string name, string what, IEnumerable<string> snippetNames, IEnumerable<string> imageNames, Func<string, bool>? isEmojiShortcode, string trigger)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return new(Severity.Error, ValidationField.Name, $"Give the {what} a name.");
        if (trimmed.Any(char.IsWhiteSpace))
            return new(Severity.Error, ValidationField.Name, "Names can't contain spaces. Use - or _ instead.");
        if (!trimmed.All(IsNameChar))
            return new(Severity.Error, ValidationField.Name, "Names can only contain letters, digits, - and _.");
        if (snippetNames.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            return new(Severity.Error, ValidationField.Name, $"There's already a snippet named “{trimmed}”.");
        if (imageNames.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            return new(Severity.Error, ValidationField.Name, $"There's already an image named “{trimmed}”.");
        if (isEmojiShortcode?.Invoke(trimmed) == true)
            return new(Severity.Warning, ValidationField.Name, $"“{TriggerFormat.Code(trigger, trimmed)}” is also an emoji. Your {what} is inserted instead");
        return null;
    }

    public static IReadOnlyList<ValidationMessage> ValidateShorthand(string abbreviation, string replacement, IEnumerable<string> otherAbbreviations)
    {
        var messages = new List<ValidationMessage>();
        var a = abbreviation.Trim();
        if (a.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Type the shorthand"));
        else if (a.Any(char.IsWhiteSpace))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Shorthands can't contain spaces"));
        else if (a.Any(c => TextClass.IsClosingPunctuation(c) || TextClass.IsOpeningPunctuation(c)))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, "Shorthands can't contain punctuation that starts or ends a word, such as . , ; : ! ? ( ) or quotes"));
        else if (otherAbbreviations.Any(o => string.Equals(o.Trim(), a, StringComparison.OrdinalIgnoreCase)))
            messages.Add(new(Severity.Error, ValidationField.Shorthand, $"“{a}” is already in the list."));
        else if (CommonWords.Contains(a))
            messages.Add(new(Severity.Warning, ValidationField.Shorthand, $"“{a}” is a common word, so it will expand very often."));

        if (replacement.Length == 0)
            messages.Add(new(Severity.Error, ValidationField.Expansion, "Type what it expands to"));
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
