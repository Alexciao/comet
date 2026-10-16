namespace Comet.Core.Settings;

/// <summary>
/// Recent-and-frequent picks. Each pick adds 1 to a score that halves every <see cref="HalfLife"/>,
/// so something picked often last week outranks something picked once last year.
/// </summary>
public sealed class Frecency
{
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    // Below this the entry is forgotten when saving.
    private const double Floor = 0.05;

    private readonly Dictionary<string, FrecencyEntry> _items;

    public Frecency(FrecencyFile? file = null) =>
        _items = new Dictionary<string, FrecencyEntry>(file?.Items ?? [], StringComparer.Ordinal);

    public int Count => _items.Count;

    public static string EmojiKey(ReadOnlySpan<char> emoji) => emoji.ToString().Replace("️", "", StringComparison.Ordinal);

    public static string SnippetKey(string snippetId) => "snippet:" + snippetId;

    public void Record(string key, DateTimeOffset now)
    {
        var score = Score(key, now) + 1;
        _items[key] = new FrecencyEntry { Score = score, LastUsed = now.ToUnixTimeSeconds() };
    }

    public double Score(string key, DateTimeOffset now) =>
        _items.TryGetValue(key, out var e) ? Decay(e, now) : 0;

    public IEnumerable<(string Key, double Score)> All(DateTimeOffset now) => _items.Select(kv => (kv.Key, Decay(kv.Value, now)));

    public void Clear() => _items.Clear();

    public FrecencyFile ToFile(DateTimeOffset now) => new()
    {
        Items = _items.Where(kv => Decay(kv.Value, now) >= Floor).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
    };

    private static double Decay(FrecencyEntry e, DateTimeOffset now)
    {
        var age = now.ToUnixTimeSeconds() - e.LastUsed;
        return age <= 0 ? e.Score : e.Score * Math.Pow(0.5, age / HalfLife.TotalSeconds);
    }
}
