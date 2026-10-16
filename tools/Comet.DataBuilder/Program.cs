using System.Globalization;
using System.Text;
using System.Text.Json;
using Comet.Core.Emoji;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

// Compiles the upstream emoji datasets in data/sources into src/Comet.Core/Data/emoji.bin.
//
//   emojibase-data.json            CLDR names, CLDR keywords (tags), skin tones, order, version
//   emojibase-shortcodes-*.json    GitHub, Slack (iamcal), emojibase and CLDR shortcodes
//   gemoji.json                    GitHub aliases and tags
//   emojilib-en-US.json            everyday keywords
//   unicode-emoji-frequency.txt    popularity ranking (Unicode Consortium)
//   ../keywords-extra.tsv          curated keywords for words people actually type
//
// Every emoji is kept; the ones this machine's Segoe UI Emoji cannot draw as one glyph are flagged, and the
// app re-checks against the installed font at load time (so a Windows font update reveals them).
//
// Options: --font <ttf>  --data-version <yyyymmddNN>  --out <path>  --sources <dir>

var root = FindRepoRoot();
var options = ParseOptions(args);
var src = options.GetValueOrDefault("sources") ?? Path.Combine(root, "data", "sources");
var fontPath = options.GetValueOrDefault("font") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
var dataVersion = options.TryGetValue("data-version", out var dv)
    ? int.Parse(dv, CultureInfo.InvariantCulture)
    : int.Parse(DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 100;
var outPath = options.GetValueOrDefault("out") ?? Path.Combine(root, "src", "Comet.Core", "Data", "emoji.bin");

using var emojibase = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(src, "emojibase-data.json")));
var shortcodeSets = ShortcodeSources
    .Select(n => LoadShortcodes(Path.Combine(src, $"emojibase-shortcodes-{n}.json")))
    .ToArray();
var gemoji = LoadGemoji(Path.Combine(src, "gemoji.json"));
var emojilib = LoadEmojilib(Path.Combine(src, "emojilib-en-US.json"));
var popularity = LoadPopularity(Path.Combine(src, "unicode-emoji-frequency.txt"));
var extra = LoadExtraKeywords(Path.Combine(root, "data", "keywords-extra.tsv"));
using var font = new FontChecker(fontPath);

var entries = new List<EmojiSourceEntry>();
var unsupported = new List<string>();
foreach (var e in emojibase.RootElement.EnumerateArray())
{
    if (!e.TryGetProperty("group", out var groupEl) || groupEl.GetInt32() == 2)
        continue; // regional indicators and components (skin tone / hair swatches)

    var hex = e.GetProperty("hexcode").GetString()!;
    var emoji = e.GetProperty("emoji").GetString()!;
    var label = e.GetProperty("label").GetString()!;
    var rendered = font.Renders(emoji);
    if (!rendered)
        unsupported.Add($"{emoji}\t{label}\tv{e.GetProperty("version").GetDouble().ToString(CultureInfo.InvariantCulture)}");

    var key = StripVs(emoji);
    var shortcodes = new List<string>();
    void AddShortcodes(IEnumerable<string>? codes)
    {
        foreach (var c in codes ?? [])
        {
            var code = c.Trim().ToLowerInvariant();
            if (code.Length > 0 && !shortcodes.Contains(code))
                shortcodes.Add(code);
        }
    }

    AddShortcodes(shortcodeSets[0].GetValueOrDefault(hex));           // GitHub
    AddShortcodes(shortcodeSets[1].GetValueOrDefault(hex));           // Slack
    AddShortcodes((gemoji.TryGetValue(key, out var gm) ? gm.Aliases : null));
    AddShortcodes(shortcodeSets[2].GetValueOrDefault(hex));           // emojibase
    AddShortcodes(shortcodeSets[3].GetValueOrDefault(hex));           // CLDR
    if (shortcodes.Count == 0)
        shortcodes.Add(SnakeCase(label));

    // Primary shortcode: the first Slack/GitHub one that starts with a letter (so 👍 shows "thumbsup", not "+1").
    var primary = shortcodes.FindIndex(s => char.IsAsciiLetter(s[0]));
    if (primary > 0)
    {
        var p = shortcodes[primary];
        shortcodes.RemoveAt(primary);
        shortcodes.Insert(0, p);
    }

    var keywords = new List<string>();
    void AddKeywords(IEnumerable<string>? words)
    {
        foreach (var w in words ?? [])
        {
            var k = w.Trim().ToLowerInvariant();
            if (k.Length == 0 || !k.Any(char.IsLetterOrDigit) || k.Contains(':', StringComparison.Ordinal))
                continue; // emoticons such as ":D" are not search keywords
            if (!keywords.Contains(k))
                keywords.Add(k);
        }
    }

    if (e.TryGetProperty("tags", out var tags))
        AddKeywords(tags.EnumerateArray().Select(t => t.GetString()!));
    AddKeywords((gemoji.TryGetValue(key, out var gt) ? gt.Tags : null));
    AddKeywords(emojilib.GetValueOrDefault(key));
    AddKeywords(extra.GetValueOrDefault(key));

    List<string>? tones = null;
    if (e.TryGetProperty("skins", out var skins))
    {
        var uniform = skins.EnumerateArray()
            .Where(s => s.GetProperty("tone").ValueKind == JsonValueKind.Number)
            .OrderBy(s => s.GetProperty("tone").GetInt32())
            .Select(s => s.GetProperty("emoji").GetString()!)
            .ToList();
        if (uniform.Count == 5 && (!rendered || uniform.All(font.Renders)))
            tones = uniform;
    }

    entries.Add(new EmojiSourceEntry(
        Emoji: emoji,
        Name: label,
        Shortcodes: shortcodes,
        Keywords: keywords,
        SkinTones: tones,
        Popularity: popularity.GetValueOrDefault(key, ushort.MaxValue),
        Order: e.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
        Group: groupEl.GetInt32(),
        IsVariant: IsVariant(hex),
        RenderedOnReferenceFont: rendered));
}

entries.Sort((a, b) => a.Order.CompareTo(b.Order));
var blob = EmojiBlobWriter.Write(entries, dataVersion);
File.WriteAllBytes(outPath, blob);

// Human-readable dump for code review and for tuning the golden set.
var dump = new StringBuilder();
dump.AppendLine("# Generated by tools/Comet.DataBuilder. emoji\tpopularity\tshortcodes\tname\tkeywords");
foreach (var x in entries)
{
    dump.Append(x.Emoji).Append('\t')
        .Append(x.Popularity == ushort.MaxValue ? "-" : x.Popularity.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(string.Join(' ', x.Shortcodes)).Append('\t')
        .Append(x.Name).Append('\t')
        .AppendLine(string.Join(", ", x.Keywords));
}

Directory.CreateDirectory(Path.Combine(root, "data", "generated"));
File.WriteAllText(Path.Combine(root, "data", "generated", "emoji-index.tsv"), dump.ToString().ReplaceLineEndings("\n"));
File.WriteAllText(Path.Combine(root, "data", "generated", "unsupported-on-reference-font.tsv"), string.Join('\n', unsupported) + "\n");

Console.WriteLine($"{entries.Count} emoji ({unsupported.Count} not drawn by {Path.GetFileName(fontPath)}), data version {dataVersion}, blob {blob.Length / 1024} KiB -> {outPath}");
return 0;

static Dictionary<string, string> ParseOptions(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            throw new ArgumentException($"Expected --name value pairs, got '{args[i]}'.");
        map[args[i][2..]] = args[i + 1];
    }

    return map;
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "Comet.sln")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("Run from inside the Comet repository.");
}

static string StripVs(string s) => s.Replace("️", "", StringComparison.Ordinal).Replace("︎", "", StringComparison.Ordinal);

static string SnakeCase(string label) =>
    string.Join('_', label.ToLowerInvariant().Split([' ', ':', ',', '.', '’', '\'', '-', '!', '“', '”', '(', ')'], StringSplitOptions.RemoveEmptyEntries));

// Gendered ZWJ forms (♀/♂) are variants of their gender-neutral base.
static bool IsVariant(string hex) => hex.Contains("200D", StringComparison.Ordinal) && (hex.Contains("2640", StringComparison.Ordinal) || hex.Contains("2642", StringComparison.Ordinal));

static Dictionary<string, string[]> LoadShortcodes(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
    var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in doc.RootElement.EnumerateObject())
    {
        map[p.Name] = p.Value.ValueKind == JsonValueKind.Array
            ? p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : [p.Value.GetString()!];
    }

    return map;
}

static Dictionary<string, (string[] Aliases, string[] Tags)> LoadGemoji(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
    var map = new Dictionary<string, (string[], string[])>();
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        map[StripVs(e.GetProperty("emoji").GetString()!)] = (
            e.GetProperty("aliases").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            e.GetProperty("tags").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    return map;
}

static Dictionary<string, string[]> LoadEmojilib(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
    var map = new Dictionary<string, string[]>();
    foreach (var p in doc.RootElement.EnumerateObject())
        map[StripVs(p.Name)] = p.Value.EnumerateArray().Select(v => v.GetString()!.Replace('_', ' ')).ToArray();
    return map;
}

static Dictionary<string, int> LoadPopularity(string path)
{
    var map = new Dictionary<string, int>();
    var rank = 0;
    foreach (var line in File.ReadLines(path))
    {
        if (line.StartsWith('#') || line.Length == 0)
            continue;
        rank++;
        map.TryAdd(StripVs(line.Trim().TrimEnd('…')), rank);
    }

    return map;
}

static Dictionary<string, string[]> LoadExtraKeywords(string path)
{
    var map = new Dictionary<string, string[]>();
    if (!File.Exists(path))
        return map;
    foreach (var line in File.ReadLines(path))
    {
        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            continue;
        var parts = line.Split('\t');
        var key = StripVs(parts[0].Trim());
        var words = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        map[key] = map.TryGetValue(key, out var existing) ? [.. existing, .. words] : words;
    }

    return map;
}

/// <summary>Shapes text with HarfBuzz against the system emoji font to see whether it renders as one glyph.</summary>
internal sealed class FontChecker : IDisposable
{
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;

    public FontChecker(string path)
    {
        _blob = Blob.FromFile(path);
        _face = new Face(_blob, 0);
        _font = new Font(_face);
    }

    private int _referenceAdvance;

    /// <summary>
    /// True when the font draws <paramref name="text"/> as one emoji: no .notdef glyph, and either a single
    /// visible glyph or a layered composition (e.g. families) no wider than one emoji. Regional-indicator
    /// pairs must ligate to a single glyph, otherwise Windows shows two letters instead of a flag.
    /// </summary>
    public bool Renders(string text)
    {
        if (_referenceAdvance == 0)
            _referenceAdvance = Shape("😀").Advance;
        var (visible, advance, notdef) = Shape(text);
        if (notdef)
            return false;
        if (visible == 1)
            return true;
        var regionalIndicator = text.EnumerateRunes().Any(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF);
        return !regionalIndicator && advance <= _referenceAdvance * 5 / 4;
    }

    private (int Visible, int Advance, bool NotDef) Shape(string text)
    {
        using var buffer = new Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        _font.Shape(buffer);
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        int visible = 0, advance = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            if (infos[i].Codepoint == 0)
                return (0, 0, true);
            if (positions[i].XAdvance > 0)
                visible++;
            advance += positions[i].XAdvance;
        }

        return (visible, advance, false);
    }

    public void Dispose()
    {
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }
}

partial class Program
{
    private static readonly string[] ShortcodeSources = ["github", "iamcal", "emojibase", "cldr"];
}
