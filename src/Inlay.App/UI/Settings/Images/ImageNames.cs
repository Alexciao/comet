using System.Globalization;
using Inlay.Core.Images;
using Inlay.Core.Settings;

namespace Inlay.App.UI.Settings.Images;

/// <summary>
/// Names Inlay makes up for the user: images added by dropping, pasting or picking a file, the Latest screenshot
/// preset and duplicates. Made-up names follow the collections' convention (<c>My Cat.png</c> → <c>my_cat</c>) and
/// are unique across snippets, images and emoji (<c>_2</c>, <c>_3</c>…). What the user types is never reformatted.
/// </summary>
internal sealed class ImageNames
{
    private readonly HashSet<string> _taken;
    private readonly Func<string, bool> _isEmojiShortcode;

    /// <param name="snippetNames">Every snippet name.</param>
    /// <param name="imageNames">Every name images use (entries and collection items).</param>
    public ImageNames(IEnumerable<string> snippetNames, IEnumerable<string> imageNames, Func<string, bool> isEmojiShortcode)
    {
        _taken = new HashSet<string>(snippetNames.Concat(imageNames).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        _isEmojiShortcode = isEmojiShortcode;
    }

    /// <summary>The name part for a file: <c>My Cat.png</c> → <c>my_cat</c>, "image" when nothing is left.</summary>
    public static string FromFile(string? fileName) =>
        fileName is null ? "image" : CollectionNaming.FromFileName(fileName) ?? "image";

    /// <summary>
    /// The names images use: image and folder entries, and every collection item Quick Insert knows (those come from
    /// <paramref name="itemNames"/>, the service's current list).
    /// </summary>
    public static IEnumerable<string> InUse(IEnumerable<ImageEntry> entries, IEnumerable<string> itemNames, string? exceptEntryId = null) =>
        entries.Where(e => e.Kind != ImageEntryKind.Collection && e.Id != exceptEntryId).Select(e => e.Name).Concat(itemNames);

    /// <summary>
    /// <paramref name="name"/>, or the first of <c>name_2</c>, <c>name_3</c>… nobody uses, and takes it (a batch of
    /// files gets distinct names). Emoji shortcodes count as used: a made-up name never hides an emoji.
    /// </summary>
    public string Take(string name)
    {
        var candidate = name;
        for (var n = 2; _taken.Contains(candidate) || _isEmojiShortcode(candidate); n++)
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{n}");
        _taken.Add(candidate);
        return candidate;
    }
}
