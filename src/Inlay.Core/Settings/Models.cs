using System.Text.Json.Serialization;

namespace Inlay.Core.Settings;

/// <summary><c>settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>2 = Inlay 1.1 (per-app Quick Insert modes, Shorthands, Images); 1 = Comet 1.0.</summary>
    public const int CurrentVersion = 2;

    public const int MaxMinimumCharacters = 4;

    public const int MinRecentsDelayMs = 300;

    public const int MaxRecentsDelayMs = 1500;

    /// <summary>
    /// Chat apps with their own <c>:</c> emoji picker. They default to <see cref="QuickInsertMode.HideEmoji"/>:
    /// Quick Insert appears there only when one of your snippets or images matches.
    /// </summary>
    public static readonly string[] ChatApps = ["slack.exe", "discord.exe", "ms-teams.exe", "teams.exe", "telegram.exe", "whatsapp.exe", "whatsapp.root.exe"];

    public int SchemaVersion { get; set; } = CurrentVersion;

    public FeatureSettings Features { get; set; } = new();

    public bool StartAtLogin { get; set; } = true;

    /// <summary>The character that opens Quick Insert (default <c>:</c>).</summary>
    public string TriggerCharacter { get; set; } = ":";

    /// <summary>Characters after the trigger before Quick Insert appears (1–4).</summary>
    public int MinimumCharacters { get; set; } = 2;

    /// <summary>0 = default yellow, 1–5 = light … dark (Fitzpatrick 1-2 … 6).</summary>
    public int SkinTone { get; set; }

    /// <summary>How Quick Insert behaves in specific apps (process file names); apps not listed show everything.</summary>
    public List<AppQuickInsertMode> QuickInsertApps { get; set; } = [.. ChatApps.Select(a => new AppQuickInsertMode { App = a, Mode = QuickInsertMode.HideEmoji })];

    /// <summary>Apps where shorthands never expand.</summary>
    public List<string> ShorthandsExcludedApps { get; set; } = [];

    /// <summary>Typing the trigger and pausing opens Quick Insert with the most used items.</summary>
    public bool ShowRecentsOnPause { get; set; } = true;

    /// <summary>How long the pause after the trigger is (300–1500 ms).</summary>
    public int RecentsDelayMs { get; set; } = 500;

    /// <summary>Global shortcut that opens Quick Insert without typing the trigger ("Win+Alt+I"); empty = off.</summary>
    public string QuickInsertShortcut { get; set; } = DefaultShortcut;

    public const string DefaultShortcut = "Win+Alt+I";

    /// <summary>When nothing matches, offer to create a snippet with the typed name.</summary>
    public bool OfferCreateSnippet { get; set; } = true;

    public bool UpdateEmojiAutomatically { get; set; } = true;

    /// <summary>Check GitHub at launch and once a day for a newer Inlay and say so in Settings and the tray menu.</summary>
    public bool NotifyAppUpdates { get; set; } = true;

    /// <summary>Learned per app: "clipboard" for apps that mishandle Unicode SendInput.</summary>
    public Dictionary<string, string> InsertionMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Learned per app: the clipboard formats an image is pasted with (see the image inserter).</summary>
    public Dictionary<string, string> ImagePasteMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mode for <paramref name="process"/> (a process file name).</summary>
    public QuickInsertMode ModeFor(string process)
    {
        foreach (var a in QuickInsertApps)
        {
            if (string.Equals(a.App, process, StringComparison.OrdinalIgnoreCase))
                return a.Mode;
        }

        return QuickInsertMode.ShowEverything;
    }

    /// <summary>Clamps values a hand edit may have broken.</summary>
    public AppSettings Normalize()
    {
        Features ??= new();
        // One UTF-16 unit that isn't a letter, digit or space (the hook sees typed characters one unit at a time).
        if (string.IsNullOrEmpty(TriggerCharacter) || char.IsWhiteSpace(TriggerCharacter[0]) || char.IsLetterOrDigit(TriggerCharacter[0]) || char.IsSurrogate(TriggerCharacter[0]))
            TriggerCharacter = ":";
        else if (TriggerCharacter.Length > 1)
            TriggerCharacter = TriggerCharacter[..1];
        MinimumCharacters = Math.Clamp(MinimumCharacters, 1, MaxMinimumCharacters);
        SkinTone = Math.Clamp(SkinTone, 0, 5);
        RecentsDelayMs = Math.Clamp(RecentsDelayMs, MinRecentsDelayMs, MaxRecentsDelayMs);
        QuickInsertShortcut ??= "";
        QuickInsertApps = [.. (QuickInsertApps ?? [])
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.App) && Enum.IsDefined(a.Mode))
            .Select(a => new AppQuickInsertMode { App = a.App.Trim().ToLowerInvariant(), Mode = a.Mode })
            .DistinctBy(a => a.App)];
        ShorthandsExcludedApps = Clean(ShorthandsExcludedApps);
        InsertionMethods = Cleanup.CaseInsensitive(InsertionMethods);
        ImagePasteMethods = Cleanup.CaseInsensitive(ImagePasteMethods);
        return this;
    }

    private static List<string> Clean(List<string>? apps) =>
        [.. (apps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()).Distinct()];
}

/// <summary>What Quick Insert shows in one app.</summary>
public enum QuickInsertMode
{
    /// <summary>Snippets, images, emoji and symbols.</summary>
    ShowEverything,
    /// <summary>Only appears when one of your own snippets or images matches (the app has its own emoji picker).</summary>
    HideEmoji,
    /// <summary>Never appears from the trigger (the keyboard shortcut still works).</summary>
    Off,
}

public sealed class AppQuickInsertMode
{
    /// <summary>Process file name, lower case ("slack.exe").</summary>
    public string App { get; set; } = "";

    public QuickInsertMode Mode { get; set; }
}

public sealed class FeatureSettings
{
    public bool Emoji { get; set; } = true;

    /// <summary>Arrows, math, currency and other symbols, part of the Emoji feature.</summary>
    public bool Symbols { get; set; } = true;

    public bool Snippets { get; set; } = true;

    public bool Images { get; set; } = true;

    public bool Shorthands { get; set; } = true;
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

/// <summary><c>shorthands.json</c> (<c>autoreplace.json</c> in Comet 1.0). Ships empty.</summary>
public sealed class ShorthandsFile
{
    /// <summary>2 = <c>shorthand</c>/<c>expansion</c>; 1 = Comet 1.0's <c>abbreviation</c>/<c>replacement</c>.</summary>
    public const int CurrentVersion = 2;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public List<ShorthandDefinition> Entries { get; set; } = [];
}

public sealed class ShorthandDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Shorthand { get; set; } = "";

    public string Expansion { get; set; } = "";

    public bool MatchCase { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary><c>images.json</c>: the user's images, folders and collections. Ships empty.</summary>
public sealed class ImagesFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public List<ImageEntry> Entries { get; set; } = [];
}

public enum ImageEntryKind
{
    /// <summary>One file, copied into the library (named by its content hash).</summary>
    Image,
    /// <summary>One name that resolves to an image in a folder when used (newest, random, or picked).</summary>
    Folder,
    /// <summary>Every image in a folder becomes its own entry, named after the file.</summary>
    Collection,
}

public enum FolderMode
{
    Newest,
    Random,
    /// <summary>Quick Insert shows the newest few as a strip to choose from.</summary>
    Pick,
}

public enum ImageInsertAs
{
    /// <summary>The image data and the file on the clipboard, then Ctrl+V.</summary>
    Image,
    /// <summary>The file only (<c>CF_HDROP</c>): attaches as a file.</summary>
    File,
    /// <summary>The full path, typed as text.</summary>
    Path,
}

public sealed class ImageEntry
{
    public const int MinPickCount = 2;
    public const int MaxPickCount = 8;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name typed after the trigger (for collections, the prefix is used instead).</summary>
    public string Name { get; set; } = "";

    /// <summary>Extra search words, separated by spaces.</summary>
    public string Keywords { get; set; } = "";

    public ImageEntryKind Kind { get; set; }

    public ImageInsertAs InsertAs { get; set; }

    /// <summary>Images: the library file ("&lt;sha256&gt;.gif").</summary>
    public string? File { get; set; }

    /// <summary>Images: the original file name, for display.</summary>
    public string? SourceName { get; set; }

    /// <summary>Folders and collections: a path, or a known folder ("{Screenshots}", "{CameraRoll}", "{Pictures}").</summary>
    public string? Folder { get; set; }

    public FolderMode Mode { get; set; }

    /// <summary>Let me pick: how many of the newest images to offer (2–8).</summary>
    public int PickCount { get; set; } = 5;

    public bool IncludeSubfolders { get; set; }

    /// <summary>Extensions to include ("png", "gif"…); empty = every supported type.</summary>
    public List<string> FileTypes { get; set; } = [];

    /// <summary>Collections: put before every file name ("gif-" → <c>:gif-thumbs-up</c>).</summary>
    public string Prefix { get; set; } = "";
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

/// <summary><c>app-update.json</c>: state of the weekly check for a newer Inlay.</summary>
public sealed class AppUpdateState
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public string? ETag { get; set; }

    public DateTimeOffset? LastChecked { get; set; }

    /// <summary>The newest release seen ("1.3.0"), kept so a 304 answer still knows what is available.</summary>
    public string? LatestVersion { get; set; }

    public string? LatestUrl { get; set; }

    /// <summary>The newest release's tag ("v1.3.0"); its notes below are replaced only when this changes.</summary>
    public string? LatestTag { get; set; }

    /// <summary>The newest release's notes (Markdown), shown only in the "See what's new" dialog.</summary>
    public string? LatestNotes { get; set; }

    public DateTimeOffset? LatestPublished { get; set; }
}

/// <summary><c>ui-state.json</c>: where the Settings window was and which page it showed. Not a setting.</summary>
public sealed class UiState
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    /// <summary>Window bounds in device-independent pixels on the desktop (null = centered default).</summary>
    public WindowBounds? SettingsWindow { get; set; }

    public bool SettingsMaximized { get; set; }

    public string? SettingsPage { get; set; }

    /// <summary>Full paths of excluded apps, by file name, so the list can show their icons and locations.</summary>
    public Dictionary<string, string> AppPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WindowBounds
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>Settings › General › Backup: everything in one file.</summary>
public sealed class BackupFile
{
    /// <summary>2 = Inlay 1.1 (inside a <c>.inlay</c> zip with the image library); 1 = Comet 1.0's JSON export.</summary>
    public const int CurrentVersion = 2;

    public int SchemaVersion { get; set; } = CurrentVersion;

    /// <summary>Always <see cref="BackupKind"/>; tells a backup apart from a snippet or replacement export.</summary>
    public string? Kind { get; set; }

    public const string BackupKind = "inlay-backup";

    public AppSettings? Settings { get; set; }

    public List<SnippetDefinition>? Snippets { get; set; }

    public List<ShorthandDefinition>? Shorthands { get; set; }

    public List<ImageEntry>? Images { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SnippetsFile))]
[JsonSerializable(typeof(ShorthandsFile))]
[JsonSerializable(typeof(ImagesFile))]
[JsonSerializable(typeof(FrecencyFile))]
[JsonSerializable(typeof(EmojiUpdateState))]
[JsonSerializable(typeof(AppUpdateState))]
[JsonSerializable(typeof(UiState))]
[JsonSerializable(typeof(BackupFile))]
public sealed partial class InlayJsonContext : JsonSerializerContext;
