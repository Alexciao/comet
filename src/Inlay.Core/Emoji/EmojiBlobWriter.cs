using System.Runtime.InteropServices;
using Inlay.Core.Text;

namespace Inlay.Core.Emoji;

/// <summary>One emoji (or symbol) as assembled by the data builder from the upstream datasets and data/symbols.tsv.</summary>
/// <param name="Shortcodes">Displayed codes, primary first.</param>
/// <param name="Curated">Everyday words mapped to this emoji, with the emoji's preference position for that word.</param>
/// <param name="SearchAliases">Codes that find the entry but are never displayed (<see cref="TermKind.Alias"/>).</param>
/// <param name="IsSymbol">A text symbol; symbols must come after every emoji.</param>
public sealed record EmojiSourceEntry(
    string Emoji,
    string Name,
    IReadOnlyList<string> Shortcodes,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<(string Word, int Position)> Curated,
    IReadOnlyList<string>? SkinTones,
    int Popularity,
    int Order,
    int Group,
    bool IsVariant,
    bool RenderedOnReferenceFont,
    IReadOnlyList<string>? SearchAliases = null,
    bool IsSymbol = false);

/// <summary>Builds the term index and serializes the dataset into the <see cref="EmojiBlob"/> layout.</summary>
public static class EmojiBlobWriter
{
    public static byte[] Write(IReadOnlyList<EmojiSourceEntry> entries, int dataVersion)
    {
        if (entries.Count > 0xFFFF)
            throw new ArgumentException("Too many emoji for the posting format.", nameof(entries));

        var chars = new List<char>(1 << 20);
        var stringCache = new Dictionary<string, StringRef>(StringComparer.Ordinal);
        StringRef Intern(string s)
        {
            if (!stringCache.TryGetValue(s, out var r))
            {
                r = new StringRef { Offset = chars.Count, Length = s.Length };
                chars.AddRange(s);
                stringCache[s] = r;
            }

            return r;
        }

        var records = new EmojiRecord[entries.Count];
        var shortcodes = new List<StringRef>();
        var tones = new List<StringRef>();
        var emojiPostings = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
        var symbolPostings = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
        var postings = emojiPostings;

        void AddPosting(string term, uint posting)
        {
            if (term.Length == 0)
                return;
            if (!postings.TryGetValue(term, out var list))
                postings[term] = list = [];
            // Keep only the strongest relation per (term, emoji).
            var emoji = Posting.Emoji(posting);
            var existing = list.FindIndex(p => Posting.Emoji(p) == emoji);
            if (existing < 0)
                list.Add(posting);
            else if (Posting.Kind(posting) < Posting.Kind(list[existing]))
                list[existing] = posting;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.IsSymbol && postings == symbolPostings)
                throw new ArgumentException("Symbols must follow every emoji.", nameof(entries));
            if (e.IsSymbol)
                postings = symbolPostings;
            ref var r = ref records[i];
            r.Emoji = Intern(e.Emoji);
            r.Name = Intern(e.Name);
            r.FirstShortcode = shortcodes.Count;
            r.ShortcodeCount = (ushort)e.Shortcodes.Count;
            r.Popularity = (ushort)Math.Clamp(e.Popularity, 1, ushort.MaxValue);
            r.Order = (ushort)Math.Clamp(e.Order, 0, ushort.MaxValue);
            r.Group = (byte)Math.Clamp(e.Group, 0, 255);
            r.Flags = (byte)((e.IsVariant ? EmojiFlags.Variant : EmojiFlags.None)
                | (e.RenderedOnReferenceFont ? EmojiFlags.RenderedOnReferenceFont : EmojiFlags.None)
                | (e.IsSymbol ? EmojiFlags.Symbol : EmojiFlags.None));
            r.FirstTone = -1;

            for (var slot = 0; slot < e.Shortcodes.Count; slot++)
            {
                var sc = e.Shortcodes[slot];
                shortcodes.Add(Intern(sc));
                var suffixes = SearchText.WordSuffixes(sc);
                for (var k = 0; k < suffixes.Count; k++)
                    AddPosting(suffixes[k], Posting.Pack(i, k == 0 ? TermKind.Shortcode : TermKind.ShortcodeWord, slot));
            }

            foreach (var alias in e.SearchAliases ?? [])
            {
                var suffixes = SearchText.WordSuffixes(alias);
                for (var k = 0; k < suffixes.Count; k++)
                    AddPosting(suffixes[k], Posting.Pack(i, k == 0 ? TermKind.Alias : TermKind.ShortcodeWord, 0));
            }

            foreach (var (word, position) in e.Curated)
                AddPosting(SearchText.Fold(word), Posting.Pack(i, TermKind.Curated, position));

            foreach (var suffix in SearchText.WordSuffixes(e.Name))
                AddPosting(suffix, Posting.Pack(i, TermKind.NameWord, 0));

            foreach (var kw in e.Keywords)
                AddPosting(SearchText.Fold(kw), Posting.Pack(i, TermKind.Keyword, 0));

            if (e.SkinTones is { Count: 5 })
            {
                r.FirstTone = tones.Count;
                foreach (var t in e.SkinTones)
                    tones.Add(Intern(t));
            }
        }

        (List<TermRecord> Terms, List<uint> Postings) Index(SortedDictionary<string, List<uint>> map)
        {
            var terms = new List<TermRecord>(map.Count);
            var postingArray = new List<uint>();
            foreach (var (term, list) in map)
            {
                terms.Add(new TermRecord { Text = Intern(term), PostingStart = postingArray.Count, PostingCount = list.Count });
                postingArray.AddRange(list);
            }

            return (terms, postingArray);
        }

        var emojiIndex = Index(emojiPostings);
        var symbolIndex = Index(symbolPostings);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        const int sectionCount = (int)EmojiBlob.Section.Count;
        var headerSize = EmojiBlob.HeaderFixedSize + (sectionCount * 8);
        w.Write(new byte[headerSize]);

        var table = new (int Offset, int Count)[sectionCount];
        void Section<T>(EmojiBlob.Section s, ReadOnlySpan<T> data) where T : unmanaged
        {
            while (ms.Position % 16 != 0)
                w.Write((byte)0);
            table[(int)s] = ((int)ms.Position, data.Length);
            w.Write(MemoryMarshal.AsBytes(data));
        }

        Section(EmojiBlob.Section.Emoji, records.AsSpan());
        Section(EmojiBlob.Section.Shortcodes, CollectionsMarshal.AsSpan(shortcodes));
        Section(EmojiBlob.Section.Tones, CollectionsMarshal.AsSpan(tones));
        Section(EmojiBlob.Section.Terms, CollectionsMarshal.AsSpan(emojiIndex.Terms));
        Section(EmojiBlob.Section.Postings, CollectionsMarshal.AsSpan(emojiIndex.Postings));
        Section(EmojiBlob.Section.SymbolTerms, CollectionsMarshal.AsSpan(symbolIndex.Terms));
        Section(EmojiBlob.Section.SymbolPostings, CollectionsMarshal.AsSpan(symbolIndex.Postings));
        Section(EmojiBlob.Section.Chars, CollectionsMarshal.AsSpan(chars));

        ms.Position = 0;
        w.Write(EmojiBlob.Magic);
        w.Write(EmojiBlob.FormatVersion);
        w.Write(dataVersion);
        w.Write(sectionCount);
        foreach (var (offset, count) in table)
        {
            w.Write(offset);
            w.Write(count);
        }

        w.Flush();
        return ms.ToArray();
    }
}
