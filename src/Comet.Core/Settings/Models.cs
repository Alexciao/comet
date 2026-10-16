using System.Text.Json.Serialization;

namespace Comet.Core.Settings;

/// <summary><c>settings.json</c>.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public FeatureSettings Features { get; set; } = new();

    public bool StartAtLogin { get; set; } = true;

    /// <summary>The character that opens the picker (default <c>:</c>).</summary>
    public string TriggerCharacter { get; set; } = ":";

    /// <summary>Characters after the trigger before the popup appears (1–5).</summary>
    public int MinimumCharacters { get; set; } = 2;

    /// <summary>0 = default yellow, 1–5 = light … dark (Fitzpatrick 1-2 … 6).</summary>
    public int SkinTone { get; set; }

    /// <summary>Apps (process file names) with their own colon picker.</summary>
    public List<string> PickerExcludedApps { get; set; } = ["slack.exe", "discord.exe", "ms-teams.exe", "teams.exe", "telegram.exe"];

    public List<string> AutoReplaceExcludedApps { get; set; } = [];

    /// <summary>Auto-replace's own pause, independent of the picker and of the tray's Pause.</summary>
    public bool AutoReplacePaused { get; set; }

    public bool UpdateEmojiAutomatically { get; set; } = true;

    /// <summary>Learned per app: "clipboard" for apps that mishandle Unicode SendInput.</summary>
    public Dictionary<string, string> InsertionMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clamps values a hand edit may have broken.</summary>
    public AppSettings Normalize()
    {
        Features ??= new();
        if (string.IsNullOrEmpty(TriggerCharacter) || char.IsWhiteSpace(TriggerCharacter[0]) || char.IsLetterOrDigit(TriggerCharacter[0]))
            TriggerCharacter = ":";
        else if (TriggerCharacter.Length > 1)
            TriggerCharacter = TriggerCharacter[..1];
        MinimumCharacters = Math.Clamp(MinimumCharacters, 1, 5);
        SkinTone = Math.Clamp(SkinTone, 0, 5);
        PickerExcludedApps = Clean(PickerExcludedApps);
        AutoReplaceExcludedApps = Clean(AutoReplaceExcludedApps);
        InsertionMethods = new Dictionary<string, string>(InsertionMethods ?? [], StringComparer.OrdinalIgnoreCase);
        return this;
    }

    private static List<string> Clean(List<string>? apps) =>
        [.. (apps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()).Distinct()];
}

public sealed class FeatureSettings
{
    public bool Emoji { get; set; } = true;

    public bool Snippets { get; set; } = true;

    public bool AutoReplace { get; set; } = true;
}

/// <summary><c>snippets.json</c>. Ships empty.</summary>
public sealed class SnippetsFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public List<SnippetDefinition> Snippets { get; set; } = [];
}

public sealed class SnippetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Text { get; set; } = "";
}

/// <summary><c>autoreplace.json</c>. Ships empty.</summary>
public sealed class AutoReplaceFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public List<AutoReplaceDefinition> Entries { get; set; } = [];
}

public sealed class AutoReplaceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Abbreviation { get; set; } = "";

    public string Replacement { get; set; } = "";

    public bool MatchCase { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary><c>frecency.json</c>: recent and frequent picks, keyed by emoji or <c>snippet:&lt;id&gt;</c>.</summary>
public sealed class FrecencyFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public Dictionary<string, FrecencyEntry> Items { get; set; } = [];
}

public sealed class FrecencyEntry
{
    public double Score { get; set; }

    /// <summary>Unix time (seconds) of the last pick.</summary>
    public long LastUsed { get; set; }
}

/// <summary><c>emoji-update.json</c>: state of the silent emoji data updates.</summary>
public sealed class EmojiUpdateState
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public string? ETag { get; set; }

    public DateTimeOffset? LastChecked { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SnippetsFile))]
[JsonSerializable(typeof(AutoReplaceFile))]
[JsonSerializable(typeof(FrecencyFile))]
[JsonSerializable(typeof(EmojiUpdateState))]
public sealed partial class CometJsonContext : JsonSerializerContext;
