using Inlay.Core.Shorthands;
using Inlay.Core.Emoji;
using Inlay.Core.Search;
using Inlay.Core.Settings;
using Inlay.Platform.Engine;
using Inlay.Platform.Images;

namespace Inlay.App.Services;

/// <summary>
/// Builds <see cref="EngineData"/> snapshots. Emoji data is loaded only while the Emoji feature is on and
/// released when it is turned off; a disabled feature contributes nothing.
/// </summary>
internal sealed class EngineDataFactory
{
    private readonly Lock _gate = new();
    private EmojiData? _emoji;
    private Dictionary<string, int>? _emojiIndexByKey;

    /// <summary>The dataset in use, or null while Emoji is off.</summary>
    public EmojiData? CurrentEmoji
    {
        get
        {
            lock (_gate)
                return _emoji;
        }
    }

    /// <summary>Supplies the dataset to use when Emoji is on (the embedded one, or a newer downloaded pack).</summary>
    public Func<EmojiData> LoadEmoji { get; set; } = EmojiData.LoadEmbedded;

    /// <summary>Applied to every engine built (hides emoji the installed font can't draw).</summary>
    public Func<EmojiData, bool[]?>? Visibility { get; set; }

    /// <summary>Replaces the dataset (after a data-pack update); the next build uses it.</summary>
    public void ReplaceEmoji(EmojiData data)
    {
        lock (_gate)
        {
            _emoji = data;
            _emojiIndexByKey = null;
        }
    }

    /// <summary>The user's images (null until the app starts them); its items join the snippets in the search index.</summary>
    public ImagesService? Images { get; set; }

    public EngineData Build(AppSettings settings, IReadOnlyList<SnippetDefinition> snippets, IReadOnlyList<ShorthandDefinition> shorthands, IReadOnlyList<(string Key, double Score)> frecency)
    {
        EmojiData? emoji = null;
        Dictionary<string, int>? index = null;
        lock (_gate)
        {
            if (settings.Features.Emoji)
            {
                _emoji ??= LoadEmoji();
                emoji = _emoji;
                index = _emojiIndexByKey ??= BuildIndex(_emoji);
            }
            else
            {
                // Drop the dataset; nothing else references it once the engine swaps to this snapshot.
                _emoji = null;
                _emojiIndexByKey = null;
            }
        }

        var snippetDefs = settings.Features.Snippets
            ? snippets.Where(s => s.Name.Length > 0).ToArray()
            : [];
        var images = settings.Features.Images ? Images : null;
        UserItem[] userItems = [.. snippetDefs.Select(UserItem.From), .. (images?.Items ?? []).Select(UserItem.From)];
        var snippetIndex = new SnippetIndex([.. userItems.Select(i => (i.Name, i.SearchText))]);
        var search = new SearchEngine(emoji, snippetIndex, includeSymbols: settings.Features.Symbols);

        if (emoji is not null && Visibility?.Invoke(emoji) is { } visible)
        {
            for (var i = 0; i < visible.Length && i < emoji.Count; i++)
                search.SetVisible(i, visible[i]);
        }

        var itemByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < userItems.Length; i++)
            itemByKey.TryAdd(userItems[i].FrecencyKey, i);
        var recents = new List<RecentItem>();
        foreach (var (key, score) in frecency)
        {
            if (index is not null && index.TryGetValue(key, out var e))
            {
                search.SetEmojiFrecency(e, (float)score);
                if (search.IsVisible(e))
                    recents.Add(new RecentItem(new SearchResult(ResultKind.Emoji, e, MatchTier.Exact, 0, 0), score));
            }
            else if (itemByKey.TryGetValue(key, out var s))
            {
                search.SetSnippetFrecency(s, (float)score);
                recents.Add(new RecentItem(new SearchResult(ResultKind.Snippet, s, MatchTier.Exact, 0, 0), score));
            }
        }

        // Most used first; ties by kind then index, so the order is stable between rebuilds.
        recents.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Result.Kind != b.Result.Kind ? a.Result.Kind.CompareTo(b.Result.Kind) : a.Result.Index.CompareTo(b.Result.Index));
        if (recents.Count > MaxRecents)
            recents.RemoveRange(MaxRecents, recents.Count - MaxRecents);

        var rules = settings.Features.Shorthands
            ? shorthands.Where(r => r.Enabled && r.Shorthand.Length > 0).Select(r => new ShorthandRule(r.Shorthand.Trim(), r.Expansion, r.MatchCase)).ToArray()
            : [];

        return new EngineData(search, userItems, rules, [.. recents], images);
    }

    /// <summary>Recents kept per build: the 8 shown plus room for picks to move up.</summary>
    private const int MaxRecents = 32;

    /// <summary>Index of an emoji by frecency key, for applying a single new pick to the live engine.</summary>
    public int EmojiIndexOf(string key)
    {
        lock (_gate)
            return _emojiIndexByKey is { } map && map.TryGetValue(key, out var i) ? i : -1;
    }

    private static Dictionary<string, int> BuildIndex(EmojiData data)
    {
        var map = new Dictionary<string, int>(data.Count, StringComparer.Ordinal);
        for (var i = 0; i < data.Count; i++)
            map.TryAdd(data.IsSymbol(i) ? Frecency.SymbolKey(data.Emoji(i)) : Frecency.EmojiKey(data.Emoji(i)), i);
        return map;
    }
}
