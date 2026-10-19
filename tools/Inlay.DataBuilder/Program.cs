using System.Globalization;
using System.Text;
using System.Text.Json;
using Inlay.Core.Emoji;
using Inlay.Core.Text;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

// Compiles the upstream emoji datasets in data/sources into src/Inlay.Core/Data/emoji.bin.
//
//   emojibase-data.json            CLDR names, CLDR keywords (tags), skin tones, order, version
//   emojibase-shortcodes-*.json    GitHub, Slack (iamcal), emojibase and CLDR shortcodes
//   gemoji.json                    GitHub aliases and tags
//   emojilib-en-US.json            everyday keywords
//   unicode-emoji-frequency.txt    popularity ranking (Unicode Consortium)
//   ../everyday-words.tsv          curated everyday words and slang, ranked like aliases
//   ../symbols.tsv                 curated text symbols (→ ° € …), stored after the emoji with their own index
//
// Every emoji is kept; the ones this machine's Segoe UI Emoji cannot draw as one glyph are flagged, and the
// app re-checks against the installed font at load time (so a Windows font update reveals them). Symbols are
// checked the same way against the text fonts they are drawn with (Segoe UI, Segoe UI Symbol, Cambria Math).
//
// Displayed codes follow EmojiCode: snake_case, unique once folded. Upstream codes are normalized ("t-rex" →
// t_rex); a code another entry owns (symbol names first, then primaries by popularity, then aliases) stays
// searchable as a hidden alias. The build fails when the result still breaks the rule.
//
// Options: --font <ttf>  --data-version <yyyymmddNN>  --out <path>  --sources <dir>
//          --manifest <path> --min-app-version <x.y.z>   also write the update manifest for the pack (CI)

var root = FindRepoRoot();
var options = ParseOptions(args);
var src = options.GetValueOrDefault("sources") ?? Path.Combine(root, "data", "sources");
var fontPath = options.GetValueOrDefault("font") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
var dataVersion = options.TryGetValue("data-version", out var dv)
    ? int.Parse(dv, CultureInfo.InvariantCulture)
    : int.Parse(DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 100;
var outPath = options.GetValueOrDefault("out") ?? Path.Combine(root, "src", "Inlay.Core", "Data", "emoji.bin");

using var emojibase = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(src, "emojibase-data.json")));
var shortcodeSets = ShortcodeSources
    .Select(n => LoadShortcodes(Path.Combine(src, $"emojibase-shortcodes-{n}.json")))
    .ToArray();
var gemoji = LoadGemoji(Path.Combine(src, "gemoji.json"));
var emojilib = LoadEmojilib(Path.Combine(src, "emojilib-en-US.json"));
var popularity = LoadPopularity(Path.Combine(src, "unicode-emoji-frequency.txt"));
var curated = LoadEverydayWords(Path.Combine(root, "data", "everyday-words.tsv"));
var curatedUsed = new HashSet<string>();
using var font = new FontChecker(fontPath);
using var textFont = new TextFontChecker(TextFontFiles.Select(f => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), f)));

var entries = new List<EmojiSourceEntry>();
var unsupported = new List<string>();
var emojiForms = new HashSet<string>(StringComparer.Ordinal);
var upstreamCodes = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var e in emojibase.RootElement.EnumerateArray())
{
    if (!e.TryGetProperty("group", out var groupEl) || groupEl.GetInt32() == 2)
        continue; // regional indicators and components (skin tone / hair swatches)

    var hex = e.GetProperty("hexcode").GetString()!;
    var emoji = e.GetProperty("emoji").GetString()!;
    var label = e.GetProperty("label").GetString()!;
    emojiForms.Add(emoji);
    var rendered = font.Renders(emoji);
    if (!rendered)
        unsupported.Add($"{emoji}\t{label}\tv{e.GetProperty("version").GetDouble().ToString(CultureInfo.InvariantCulture)}");

    var key = StripVs(emoji);
    var shortcodes = new List<string>();
    var aliases = new List<string>();
    var upstream = new List<string>();
    void AddShortcodes(IEnumerable<string>? codes)
    {
        foreach (var c in codes ?? [])
        {
            var raw = c.Trim().ToLowerInvariant();
            if (raw.Length == 0 || upstream.Contains(raw))
                continue;
            upstream.Add(raw);
            if (EmojiCode.IsSignedNumber(raw))
            {
                aliases.Add(raw); // "+1": typeable, never displayed
                continue;
            }

            // "t-rex" → t_rex. Spellings that fold the same are one code: the first is shown ("thumbsup"), the others
            // ("thumbs_up") stay searchable for their word suffixes.
            var code = EmojiCode.Normalize(raw);
            if (code.Length == 0)
                continue;
            var same = shortcodes.FindIndex(s => SearchText.Fold(s) == SearchText.Fold(code));
            if (same < 0)
                shortcodes.Add(code);
            else if (code != shortcodes[same])
                aliases.Add(code);
        }
    }

    AddShortcodes(shortcodeSets[0].GetValueOrDefault(hex));           // GitHub
    AddShortcodes(shortcodeSets[1].GetValueOrDefault(hex));           // Slack
    AddShortcodes((gemoji.TryGetValue(key, out var gm) ? gm.Aliases : null));
    AddShortcodes(shortcodeSets[2].GetValueOrDefault(hex));           // emojibase
    AddShortcodes(shortcodeSets[3].GetValueOrDefault(hex));           // CLDR
    if (shortcodes.Count == 0)
        shortcodes.Add(EmojiCode.Normalize(label));
    upstreamCodes[emoji] = string.Join(' ', upstream);

    // Primary shortcode: the first Slack/GitHub one that starts with a letter.
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
        Curated: CuratedFor(key),
        SkinTones: tones,
        Popularity: popularity.GetValueOrDefault(key, ushort.MaxValue),
        Order: e.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
        Group: groupEl.GetInt32(),
        IsVariant: IsVariant(hex),
        RenderedOnReferenceFont: rendered,
        SearchAliases: aliases));
}

IReadOnlyList<(string Word, int Position)> CuratedFor(string key)
{
    if (!curated.TryGetValue(key, out var words))
        return [];
    curatedUsed.Add(key);
    return words;
}

foreach (var missing in curated.Keys.Except(curatedUsed))
    Console.WriteLine($"warning: everyday-words.tsv references unknown emoji {missing}");

entries.Sort((a, b) => a.Order.CompareTo(b.Order));

var symbols = LoadSymbols(Path.Combine(root, "data", "symbols.tsv"), emojiForms);
var firstSymbolOrder = entries.Count == 0 ? 0 : entries.Max(x => x.Order) + 1;
var hiddenSymbols = new List<string>();
for (var i = 0; i < symbols.Count; i++)
{
    var s = symbols[i];
    var rendered = textFont.Renders(s.Symbol);
    if (!rendered)
        hiddenSymbols.Add($"{s.Symbol} {s.Names[0]}");
    entries.Add(new EmojiSourceEntry(s.Symbol, s.Display, s.Names, s.Keywords, [], null, s.Popularity, firstSymbolOrder + i, SymbolGroup,
        IsVariant: false, RenderedOnReferenceFont: rendered, IsSymbol: true));
}

entries = AssignCodes(entries);
CheckCodes(entries);
ReportCodeChanges(entries, upstreamCodes);

var blob = EmojiBlobWriter.Write(entries, dataVersion);
File.WriteAllBytes(outPath, blob);
if (options.TryGetValue("manifest", out var manifestPath))
{
    var manifest = EmojiPackManifest.Describe(blob, options.GetValueOrDefault("min-app-version") ?? "1.0.0");
    File.WriteAllBytes(manifestPath, manifest.ToJson());
    Console.WriteLine($"manifest: data {manifest.DataVersion}, {manifest.Size} bytes, sha256 {manifest.Sha256}, content {manifest.ContentHash}");
}

// Human-readable dump for code review and for tuning the golden set.
var dump = new StringBuilder();
dump.AppendLine("# Generated by tools/Inlay.DataBuilder. emoji\tpopularity\tshortcodes\tname\tkeywords");
foreach (var x in entries)
{
    dump.Append(x.Emoji).Append('\t')
        .Append(x.Popularity == ushort.MaxValue ? "-" : x.Popularity.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(string.Join(' ', x.Shortcodes));
    if (x.SearchAliases is { Count: > 0 } hidden)
        dump.Append(" [").Append(string.Join(' ', hidden)).Append(']');
    dump.Append('\t')
        .Append(x.Name).Append('\t')
        .AppendLine(string.Join(", ", x.Keywords));
}

Directory.CreateDirectory(Path.Combine(root, "data", "generated"));
File.WriteAllText(Path.Combine(root, "data", "generated", "emoji-index.tsv"), dump.ToString().ReplaceLineEndings("\n"));
File.WriteAllText(Path.Combine(root, "data", "generated", "unsupported-on-reference-font.tsv"),
    string.Join('\n', unsupported.Concat(hiddenSymbols.Select(s => s + "\tsymbol, not in " + string.Join(", ", TextFontFiles)))) + "\n");

Console.WriteLine($"{entries.Count - symbols.Count} emoji ({unsupported.Count} not drawn by {Path.GetFileName(fontPath)}), " +
    $"{symbols.Count} symbols ({hiddenSymbols.Count} not drawn by the text fonts), data version {dataVersion}, blob {blob.Length / 1024} KiB -> {outPath}");
return 0;

// Gives every displayed code one owner: symbol names first (curated, so a clash between two is an error), then emoji
// primaries by popularity, then the other aliases slot by slot. A code that loses stays searchable as a hidden alias.
static List<EmojiSourceEntry> AssignCodes(List<EmojiSourceEntry> entries)
{
    var owner = new Dictionary<string, int>(StringComparer.Ordinal);
    var kept = entries.Select(_ => new List<string>()).ToArray();
    var hidden = entries.Select(x => new List<string>(x.SearchAliases ?? [])).ToArray();
    for (var i = 0; i < entries.Count; i++)
    {
        if (!entries[i].IsSymbol)
            continue;
        foreach (var name in entries[i].Shortcodes)
        {
            if (!owner.TryAdd(SearchText.Fold(name), i))
                throw new InvalidDataException($"symbols.tsv: '{name}' ({entries[i].Emoji}) clashes with {entries[owner[SearchText.Fold(name)]].Emoji}.");
            kept[i].Add(name);
        }
    }

    var emoji = Enumerable.Range(0, entries.Count).Where(i => !entries[i].IsSymbol)
        .OrderBy(i => entries[i].Popularity).ThenBy(i => entries[i].Order).ToArray();
    var slots = emoji.Max(i => entries[i].Shortcodes.Count);
    for (var slot = 0; slot < slots; slot++)
    {
        foreach (var i in emoji)
        {
            if (slot >= entries[i].Shortcodes.Count)
                continue;
            var code = entries[i].Shortcodes[slot];
            if (owner.TryAdd(SearchText.Fold(code), i))
                kept[i].Add(code);
            else
                hidden[i].Add(code);
        }
    }

    for (var i = 0; i < entries.Count; i++)
    {
        if (kept[i].Count > 0)
            continue;
        // Every code went to another entry: take back the primary from an emoji that has others (🍨 ice_cream from
        // 🍦 icecream, which keeps soft_ice_cream), else fall back to the name.
        var code = entries[i].Shortcodes[0];
        var key = SearchText.Fold(code);
        if (owner.TryGetValue(key, out var other) && !entries[other].IsSymbol && kept[other].Count > 1)
        {
            var lost = kept[other].Find(c => SearchText.Fold(c) == key)!;
            kept[other].Remove(lost);
            hidden[other].Add(lost);
            hidden[i].Remove(code);
            owner[key] = i;
        }
        else if (!owner.TryAdd(SearchText.Fold(code = EmojiCode.Normalize(entries[i].Name)), i))
        {
            throw new InvalidDataException($"{entries[i].Emoji} has no code of its own ({string.Join(' ', entries[i].Shortcodes)}).");
        }

        kept[i].Add(code);
    }

    return [.. entries.Select((x, i) => x with { Shortcodes = kept[i], SearchAliases = hidden[i] })];
}

// The rule the app relies on (and EmojiCodeTests re-checks on the shipped data): fail the build when it is broken.
static void CheckCodes(List<EmojiSourceEntry> entries)
{
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);
    var errors = new List<string>();
    foreach (var x in entries)
    {
        foreach (var code in x.Shortcodes)
        {
            if (!EmojiCode.IsValid(code))
                errors.Add($"{x.Emoji} '{code}' is not snake_case");
            else if (!seen.TryAdd(SearchText.Fold(code), $"{x.Emoji} '{code}'"))
                errors.Add($"{x.Emoji} '{code}' collides with {seen[SearchText.Fold(code)]}");
        }
    }

    if (errors.Count > 0)
        throw new InvalidDataException("Built-in codes break the snake_case rule:\n  " + string.Join("\n  ", errors));
}

static void ReportCodeChanges(List<EmojiSourceEntry> entries, Dictionary<string, string> upstreamCodes)
{
    int changed = 0, primaryChanged = 0;
    var examples = new List<string>();
    foreach (var x in entries.Where(x => !x.IsSymbol))
    {
        var before = upstreamCodes[x.Emoji].Split(' ').Where(c => !EmojiCode.IsSignedNumber(c)).ToArray();
        if (before.SequenceEqual(x.Shortcodes))
            continue;
        changed++;
        if (before.Length > 0 && before.Where(c => char.IsAsciiLetter(c[0])).DefaultIfEmpty(before[0]).First() != x.Shortcodes[0])
        {
            primaryChanged++;
            if (examples.Count < 12)
                examples.Add($"{x.Emoji} {before.Where(c => char.IsAsciiLetter(c[0])).DefaultIfEmpty(before[0]).First()} → {x.Shortcodes[0]}");
        }
    }

    Console.WriteLine($"codes: {changed} emoji show other codes than upstream ({primaryChanged} another primary: {string.Join(", ", examples)})");
}

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
    while (dir is not null && !File.Exists(Path.Combine(dir, "Inlay.sln")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("Run from inside the Inlay repository.");
}

static string StripVs(string s) => s.Replace("️", "", StringComparison.Ordinal).Replace("︎", "", StringComparison.Ordinal);

// Rows "name [alias ...]<TAB>symbol<TAB>popularity<TAB>display<TAB>keywords" after a header line; '#' starts a comment.
static List<SymbolRow> LoadSymbols(string path, HashSet<string> emojiForms)
{
    var rows = new List<SymbolRow>();
    var errors = new List<string>();
    var lineNumber = 0;
    var header = true;
    foreach (var line in File.ReadLines(path))
    {
        lineNumber++;
        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            continue;
        if (header)
        {
            header = false; // name, symbol, popularity, display, keywords
            continue;
        }

        var f = line.Split('\t');
        if (f.Length != 5 || !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var popularity) || popularity < 1)
        {
            errors.Add($"line {lineNumber}: expected name, symbol, popularity, display, keywords");
            continue;
        }

        var names = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var symbol = f[1];
        foreach (var name in names.Where(n => !EmojiCode.IsValid(n)))
            errors.Add($"line {lineNumber}: '{name}' is not snake_case");
        if (names.Length == 0 || symbol.Length == 0 || f[3].Trim().Length == 0)
            errors.Add($"line {lineNumber}: name, symbol and display name are required");
        // An emoji-presentation character (➕ ✅) or a VS16 sequence draws as a color emoji wherever it is inserted.
        if (symbol.Contains('️', StringComparison.Ordinal) || emojiForms.Contains(symbol))
            errors.Add($"line {lineNumber}: {symbol} draws as an emoji; symbols must be text");
        if (rows.Find(r => r.Symbol == symbol) is { } duplicate)
            errors.Add($"line {lineNumber}: {symbol} is already listed as {duplicate.Names[0]}");
        rows.Add(new SymbolRow(names, symbol, popularity, f[3].Trim(), f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    if (errors.Count > 0)
        throw new InvalidDataException("symbols.tsv:\n  " + string.Join("\n  ", errors));
    return rows;
}

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

// Returns emoji -> [(word, position)] from lines "word<TAB>emoji emoji ...". Repeated words append.
static Dictionary<string, List<(string Word, int Position)>> LoadEverydayWords(string path)
{
    var byWord = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(path))
    {
        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            continue;
        var parts = line.Split('\t');
        var word = parts[0].Trim().ToLowerInvariant();
        if (!byWord.TryGetValue(word, out var list))
            byWord[word] = list = [];
        foreach (var emoji in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = StripVs(emoji);
            if (!list.Contains(key))
                list.Add(key);
        }
    }

    var map = new Dictionary<string, List<(string, int)>>();
    foreach (var (word, emojis) in byWord)
    {
        for (var i = 0; i < emojis.Count; i++)
        {
            if (!map.TryGetValue(emojis[i], out var list))
                map[emojis[i]] = list = [];
            list.Add((word, i));
        }
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

/// <summary>
/// Whether the text fonts symbols are drawn with have a glyph for every code point of a symbol, like the app's
/// DirectWrite check: a code point counts when any of the fonts (any face of a collection) has it. Missing files are skipped.
/// </summary>
internal sealed class TextFontChecker : IDisposable
{
    private readonly List<Blob> _blobs = [];
    private readonly List<(Face Face, Font Font)> _faces = [];

    public TextFontChecker(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            var blob = Blob.FromFile(path);
            _blobs.Add(blob);
            for (var i = 0; i < Math.Max(blob.FaceCount, 1); i++)
            {
                var face = new Face(blob, i);
                _faces.Add((face, new Font(face)));
            }
        }

        if (_faces.Count == 0)
            throw new FileNotFoundException("None of the text fonts for symbols is installed.");
    }

    public bool Renders(string text) =>
        text.EnumerateRunes().All(r => r.Value is 0xFE0E || _faces.Exists(f => f.Font.TryGetGlyph(r.Value, out _)));

    public void Dispose()
    {
        foreach (var (face, font) in _faces)
        {
            font.Dispose();
            face.Dispose();
        }

        foreach (var blob in _blobs)
            blob.Dispose();
    }
}

internal sealed record SymbolRow(string[] Names, string Symbol, int Popularity, string Display, string[] Keywords);

partial class Program
{
    /// <summary>Segoe UI, Segoe UI Symbol, and Cambria Math (in cambria.ttc): what symbols are drawn with.</summary>
    private static readonly string[] TextFontFiles = ["segoeui.ttf", "seguisym.ttf", "cambria.ttc"];

    /// <summary>Emoji groups are 0–9 (emojibase); symbols get their own.</summary>
    private const int SymbolGroup = 10;

    private static readonly string[] ShortcodeSources = ["github", "iamcal", "emojibase", "cldr"];
}
