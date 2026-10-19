using System.Collections.Frozen;
using Inlay.Core.Shorthands;
using Inlay.Core.Emoji;
using Inlay.Core.Search;
using Inlay.Core.Settings;
using Inlay.Platform.Native;

namespace Inlay.Platform.Engine;

/// <summary>Settings the engine needs, as an immutable snapshot swapped in atomically.</summary>
internal sealed record EngineConfig
{
    public bool Paused { get; init; }

    public bool EmojiEnabled { get; init; } = true;

    public bool SnippetsEnabled { get; init; } = true;

    public bool ShorthandsEnabled { get; init; } = true;

    public char TriggerChar { get; init; } = ':';

    /// <summary><see cref="TriggerChar"/> as a string, made once (Quick Insert shows it in every row).</summary>
    public string TriggerString { get; init; } = ":";

    public int MinChars { get; init; } = 2;

    public int SkinTone { get; init; }

    /// <summary>Open Quick Insert with recent items when the user pauses after the trigger.</summary>
    public bool ShowRecents { get; init; } = true;

    public int RecentsDelayMs { get; init; } = 500;

    /// <summary>Offer "Create snippet :name" when nothing of the user's matches the query exactly.</summary>
    public bool OfferCreateSnippet { get; init; } = true;

    /// <summary>Per-app Quick Insert modes (apps not listed show everything).</summary>
    public FrozenDictionary<string, QuickInsertMode> QuickInsertModes { get; init; } = FrozenDictionary<string, QuickInsertMode>.Empty;

    public FrozenSet<string> ShorthandsExcluded { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Process name → "clipboard" for apps that mishandle Unicode key events.</summary>
    public FrozenDictionary<string, string> InsertionMethods { get; init; } = FrozenDictionary<string, string>.Empty;

    public bool ImagesEnabled { get; init; } = true;

    public bool QuickInsertEnabled => !Paused && (EmojiEnabled || SnippetsEnabled || ImagesEnabled);

    public QuickInsertMode ModeFor(string process) =>
        QuickInsertModes.TryGetValue(process, out var mode) ? mode : QuickInsertMode.ShowEverything;

    public bool ShorthandsActive => !Paused && ShorthandsEnabled;

    /// <summary>With nothing to do the hook is uninstalled entirely.</summary>
    public bool NeedsHook => QuickInsertEnabled || ShorthandsActive;

    public static EngineConfig From(AppSettings s, bool paused) => new()
    {
        Paused = paused,
        EmojiEnabled = s.Features.Emoji,
        SnippetsEnabled = s.Features.Snippets,
        ShorthandsEnabled = s.Features.Shorthands,
        TriggerChar = s.TriggerCharacter[0],
        TriggerString = s.TriggerCharacter[..1],
        MinChars = s.MinimumCharacters,
        SkinTone = s.SkinTone,
        ShowRecents = s.ShowRecentsOnPause,
        RecentsDelayMs = s.RecentsDelayMs,
        OfferCreateSnippet = s.OfferCreateSnippet,
        ImagesEnabled = s.Features.Images,
        QuickInsertModes = s.QuickInsertApps.DistinctBy(a => a.App, StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(a => a.App, a => a.Mode, StringComparer.OrdinalIgnoreCase),
        ShorthandsExcluded = s.ShorthandsExcludedApps.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        InsertionMethods = s.InsertionMethods.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>What one of the user's own Quick Insert entries is.</summary>
internal enum UserItemKind : byte
{
    Snippet,
    /// <summary>An image entry: one file, or a folder resolved when it is used.</summary>
    Image,
    /// <summary>One file of a collection, named after the file.</summary>
    CollectionImage,
}

/// <summary>
/// One of the user's own entries, in the order of the search engine's snippet index (snippets and images share
/// that index: same fuzzy matching and ranking, and they win over emoji).
/// </summary>
internal sealed record UserItem(UserItemKind Kind, string Name, string FrecencyKey, SnippetDefinition? Snippet = null, ImageEntry? Image = null, string? File = null)
{
    public static UserItem From(SnippetDefinition s) => new(UserItemKind.Snippet, s.Name, Frecency.SnippetKey(s.Id), Snippet: s);
}

/// <summary>
/// What Quick Insert searches and shorthands match. Built off the hook thread (loading emoji data, building
/// indexes) and handed over whole; the engine swaps it only while Quick Insert is closed.
/// </summary>
internal sealed class EngineData(SearchEngine search, UserItem[] userItems, ShorthandRule[] rules, RecentItem[]? recents = null)
{
    public static EngineData Empty { get; } = new(new SearchEngine(null, null), [], []);

    public SearchEngine Search { get; } = search;

    public EmojiData? Emoji => Search.Emoji;

    /// <summary>Snippets and images, in the same order as the search engine's snippet index.</summary>
    public UserItem[] UserItems { get; } = userItems;

    public ShorthandRule[] Rules { get; } = rules;

    /// <summary>Everything picked before, most used first (frecency), across every enabled kind.</summary>
    public RecentItem[] Recents { get; } = recents ?? [];
}

/// <param name="Result">The item as a search result (emoji index, or user item index as a snippet result).</param>
/// <param name="Score">Frecency score at build time; picks add to it live.</param>
internal readonly record struct RecentItem(SearchResult Result, double Score);

/// <summary>Everything the popup needs to draw one update. Owned by the engine; the sink copies it under <see cref="Gate"/>.</summary>
internal sealed class PopupFrame
{
    public const int MaxResults = 24;

    public readonly Lock Gate = new();
    public readonly SearchResult[] Results = new SearchResult[MaxResults];
    public readonly char[] Query = new char[32];

    public int Version;
    public int Count;
    public int Selected;
    public int QueryLength;
    public RECT Anchor;
    public int SkinTone;
    public string Trigger = ":";
    public EngineData Data = EngineData.Empty;

    /// <summary>The list is the user's recent items (each row shows a history icon).</summary>
    public bool Recents;

    /// <summary>Opened by the keyboard shortcut: a search line with the query sits above the rows.</summary>
    public bool FromShortcut;

    /// <summary>The last row is "Create snippet :query" (counted in <see cref="Count"/>).</summary>
    public bool CreateRow;

    /// <summary>No caret: <see cref="Anchor"/> is the foreground window, and Quick Insert sits at its bottom center.</summary>
    public bool AnchorIsWindow;

    /// <summary>Stopwatch timestamp of the keystroke that caused this frame (for keystroke→render latency).</summary>
    public long KeyTimestamp;
}

/// <summary>Where the popup is on screen, published by the UI for click hit-testing on the hook thread.</summary>
/// <summary>
/// Where the popup's rows are on screen, for the frame <paramref name="Version"/> it shows. A class, so the hook
/// thread always reads a whole value the UI thread published, never half of an old one and half of a new one.
/// </summary>
internal sealed record PopupGeometry(RECT Window, int ListTop, int RowHeight, int FirstVisible, int VisibleRows, int Version);

/// <summary>The popup as seen from the hook thread. Calls must return immediately.</summary>
internal interface IPopupSink
{
    /// <summary>A new frame is ready (read it under <see cref="PopupFrame.Gate"/>).</summary>
    void Update(PopupFrame frame);

    /// <summary>Hide now, with no animation (also used right before inserting).</summary>
    void Hide();

    /// <summary>Current on-screen geometry, or null while hidden.</summary>
    PopupGeometry? Geometry { get; }
}

/// <summary>Allocation-free latency recorder (microseconds) with percentiles on demand.</summary>
internal sealed class LatencyStats(string name, int capacity = 4096)
{
    private readonly double[] _samples = new double[capacity];
    private readonly Lock _gate = new();
    private int _count;
    private int _next;

    public string Name { get; } = name;

    public int Count => _count;

    public void Add(double microseconds)
    {
        lock (_gate)
        {
            _samples[_next] = microseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }
    }

    public (double P50, double P95, double P99, double Max, int Count) Percentiles()
    {
        double[] copy;
        lock (_gate)
            copy = _samples[.._count];
        if (copy.Length == 0)
            return (0, 0, 0, 0, 0);
        Array.Sort(copy);
        double At(double p) => copy[Math.Min(copy.Length - 1, (int)Math.Ceiling(p * copy.Length) - 1)];
        return (At(0.50), At(0.95), At(0.99), copy[^1], copy.Length);
    }

    public string Describe()
    {
        var (p50, p95, p99, max, count) = Percentiles();
        return FormattableString.Invariant($"{Name}: n={count} p50={p50:F1}µs p95={p95:F1}µs p99={p99:F1}µs max={max:F1}µs");
    }
}
