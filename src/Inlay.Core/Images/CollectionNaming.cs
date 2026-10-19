using System.Globalization;
using System.Text;

namespace Inlay.Core.Images;

/// <summary>One file of a collection with the name Quick Insert knows it by.</summary>
public readonly record struct CollectionItem(string Name, ImageFile File);

/// <summary>
/// Names for a collection's files. Generated names follow the built-in codes' convention, lower-case
/// <c>snake_case</c> (<c>Thumbs Up!.gif</c> → <c>thumbs_up</c>); the user's prefix is used as typed.
/// </summary>
public static class CollectionNaming
{
    /// <summary>
    /// The name part derived from a file name: ASCII letters (accents dropped) and digits, lower case, every other
    /// run of characters one underscore, none at the ends. Null when nothing is left (a name in another script).
    /// </summary>
    public static string? FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(stem.Length);
        var gap = false;
        foreach (var c in stem)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (gap && sb.Length > 0)
                    sb.Append('_');
                gap = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '’' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                // "don't" → "dont", "café" → "cafe"
            }
            else
            {
                gap = true;
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Every file of the given types with its name: <paramref name="prefix"/> + the derived name. Repeated names
    /// (and names <paramref name="isTaken"/> reports as used by snippets or other images) get <c>_2</c>, <c>_3</c>…
    /// in a fixed order (by derived name, then path), so the same files always get the same names.
    /// Files whose name yields nothing are left out.
    /// </summary>
    public static List<CollectionItem> Name(IReadOnlyList<ImageFile> files, string prefix, ImageType types, Func<string, bool>? isTaken = null)
    {
        var named = new List<(string Base, ImageFile File)>(files.Count);
        foreach (var f in files)
        {
            if ((f.Type & types) != 0 && FromFileName(f.Name) is { } b)
                named.Add((b, f));
        }

        named.Sort(static (x, y) =>
        {
            var c = string.CompareOrdinal(x.Base, y.Base);
            return c != 0 ? c : string.Compare(x.File.Path, y.File.Path, StringComparison.OrdinalIgnoreCase);
        });

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CollectionItem>(named.Count);
        foreach (var (b, f) in named)
        {
            var name = prefix + b;
            for (var n = 2; used.Contains(name) || isTaken?.Invoke(name) == true; n++)
                name = string.Create(CultureInfo.InvariantCulture, $"{prefix}{b}_{n}");
            used.Add(name);
            items.Add(new(name, f));
        }

        return items;
    }
}
