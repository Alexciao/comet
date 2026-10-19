using System.Collections.Frozen;
using Comet.Core.AutoReplace;
using Comet.Core.Emoji;
using Comet.Core.Search;
using Comet.Core.Settings;
using Comet.Platform.Native;

namespace Comet.Platform.Engine;

/// <summary>Settings the engine needs, as an immutable snapshot swapped in atomically.</summary>
internal sealed record EngineConfig
{
    public bool Paused { get; init; }

    public bool EmojiEnabled { get; init; } = true;

    public bool SnippetsEnabled { get; init; } = true;

    public bool AutoReplaceEnabled { get; init; } = true;

    public char TriggerChar { get; init; } = ':';

    public int MinChars { get; init; } = 2;

    public int SkinTone { get; init; }

    public FrozenSet<string> PickerExcluded { get; init; } = FrozenSet<string>.Empty;

    public FrozenSet<string> AutoReplaceExcluded { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Process name → "clipboard" for apps that mishandle Unicode key events.</summary>
    public FrozenDictionary<string, string> InsertionMethods { get; init; } = FrozenDictionary<string, string>.Empty;

    public bool PickerEnabled => !Paused && (EmojiEnabled || SnippetsEnabled);

    public bool AutoReplaceActive => !Paused && AutoReplaceEnabled;

    /// <summary>With nothing to do the hook is uninstalled entirely.</summary>
    public bool NeedsHook => PickerEnabled || AutoReplaceActive;

    public static EngineConfig From(AppSettings s, bool paused) => new()
    {
        Paused = paused,
        EmojiEnabled = s.Features.Emoji,
        SnippetsEnabled = s.Features.Snippets,
        AutoReplaceEnabled = s.Features.AutoReplace,
        TriggerChar = s.TriggerCharacter[0],
        MinChars = s.MinimumCharacters,
        SkinTone = s.SkinTone,
        PickerExcluded = s.PickerExcludedApps.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        AutoReplaceExcluded = s.AutoReplaceExcludedApps.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        InsertionMethods = s.InsertionMethods.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// What the picker searches and auto-replace matches. Built off the hook thread (loading emoji data, building
/// indexes) and handed over whole; the engine swaps it only while the popup is closed.
/// </summary>
internal sealed class EngineData(SearchEngine search, SnippetDefinition[] snippets, AutoReplaceRule[] rules)
{
    public static EngineData Empty { get; } = new(new SearchEngine(null, null), [], []);

    public SearchEngine Search { get; } = search;

    public EmojiData? Emoji => Search.Emoji;

    /// <summary>Snippets in the same order as the search engine's snippet index.</summary>
    public SnippetDefinition[] Snippets { get; } = snippets;

    public AutoReplaceRule[] Rules { get; } = rules;
}

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
    public EngineData Data = EngineData.Empty;

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
