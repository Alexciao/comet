using Comet.Core.Text;

namespace Comet.Core.Search;

/// <summary>Search terms for the user's snippets. Rebuilt whenever the snippet list changes (rare), never per keystroke.</summary>
public sealed class SnippetIndex
{
    private const int MaxTextWords = 64;

    private readonly Entry[] _entries;

    public SnippetIndex(IReadOnlyList<(string Name, string Text)> snippets)
    {
        _entries = new Entry[snippets.Count];
        for (var i = 0; i < snippets.Count; i++)
        {
            var (name, text) = snippets[i];
            var words = new List<string>();
            foreach (var w in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = SearchText.Fold(w);
                if (f.Length > 0 && !words.Contains(f))
                    words.Add(f);
                if (words.Count == MaxTextWords)
                    break;
            }

            _entries[i] = new Entry(SearchText.Fold(name), [.. SearchText.WordSuffixes(name)], [.. words]);
        }
    }

    public static SnippetIndex Empty { get; } = new([]);

    public int Count => _entries.Length;

    internal ref readonly Entry this[int index] => ref _entries[index];

    /// <summary>Index of the snippet whose folded name equals <paramref name="folded"/>, or -1.</summary>
    internal int FindByFoldedName(ReadOnlySpan<char> folded)
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            if (folded.SequenceEqual(_entries[i].FoldedName))
                return i;
        }

        return -1;
    }

    internal readonly record struct Entry(string FoldedName, string[] NameSuffixes, string[] TextWords);
}
