using Inlay.Core.Shorthands;
using Inlay.Core.Emoji;
using Inlay.Core.Search;
using Inlay.Core.Settings;
using Inlay.Platform.Engine;

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
        var snippetIndex = new SnippetIndex([.. snippetDefs.Select(s => (s.Name, s.Text))]);
        var search = new SearchEngine(emoji, snippetIndex);

        if (emoji is not null && Visibility?.Invoke(emoji) is { } visible)
        {
            for (var i = 0; i < visible.Length && i < emoji.Count; i++)
                search.SetVisible(i, visible[i]);
        }

        var snippetById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < snippetDefs.Length; i++)
            snippetById.TryAdd(Frecency.SnippetKey(snippetDefs[i].Id), i);
        foreach (var (key, score) in frecency)
        {
            if (index is not null && index.TryGetValue(key, out var e))
                search.SetEmojiFrecency(e, (float)score);
            else if (snippetById.TryGetValue(key, out var s))
                search.SetSnippetFrecency(s, (float)score);
        }

        var rules = settings.Features.Shorthands
            ? shorthands.Where(r => r.Enabled && r.Shorthand.Length > 0).Select(r => new ShorthandRule(r.Shorthand.Trim(), r.Expansion, r.MatchCase)).ToArray()
            : [];

        return new EngineData(search, snippetDefs, rules);
    }

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
            map.TryAdd(Frecency.EmojiKey(data.Emoji(i)), i);
        return map;
    }
}
