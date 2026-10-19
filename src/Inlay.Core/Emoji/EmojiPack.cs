using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inlay.Core.Emoji;

/// <summary>
/// <c>manifest.json</c> published next to an emoji data pack: what the pack is and how to verify it.
/// </summary>
/// <param name="FormatVersion">Binary format of the pack (<see cref="EmojiData.SupportedFormatVersion"/>).</param>
/// <param name="DataVersion">Monotonic data version (yyyyMMdd × 100 + build); newer packs have larger numbers.</param>
/// <param name="MinAppVersion">Oldest app version that may load the pack (major.minor.patch).</param>
/// <param name="Size">Pack size in bytes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the pack.</param>
/// <param name="EmojiCount">Number of emoji in the pack (informational; the pack itself is checked).</param>
/// <param name="ContentHash">SHA-256 of the pack with its data version zeroed: equal hashes mean the same data, so
/// the publishing workflow skips rebuilds that changed nothing but the date.</param>
public sealed record EmojiPackManifest(int FormatVersion, int DataVersion, string MinAppVersion, long Size, string Sha256, int EmojiCount, string? ContentHash = null)
{
    /// <summary>Parses a manifest; null when it is not valid JSON or misses required fields.</summary>
    public static EmojiPackManifest? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var m = JsonSerializer.Deserialize(json, EmojiPackJsonContext.Default.EmojiPackManifest);
            return m is { FormatVersion: > 0, DataVersion: > 0, Size: > 0, Sha256.Length: 64, MinAppVersion.Length: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, EmojiPackJsonContext.Default.EmojiPackManifest);

    /// <summary>A manifest describing <paramref name="pack"/> (used by the data builder).</summary>
    public static EmojiPackManifest Describe(ReadOnlySpan<byte> pack, string minAppVersion)
    {
        using var data = EmojiData.FromBytes(pack);
        var unversioned = pack.ToArray();
        BitConverter.TryWriteBytes(unversioned.AsSpan(8), 0);
        return new EmojiPackManifest(data.FormatVersion, data.DataVersion, minAppVersion, pack.Length, EmojiPack.Hash(pack), data.Count, EmojiPack.Hash(unversioned));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(EmojiPackManifest))]
internal sealed partial class EmojiPackJsonContext : JsonSerializerContext;

/// <summary>Why a manifest or pack was not used.</summary>
public enum PackVerdict
{
    Accept,
    /// <summary>Not newer than the data in use: nothing to download.</summary>
    NotNewer,
    /// <summary>A binary format this app can't read (a newer app is needed).</summary>
    FormatTooNew,
    /// <summary>The manifest asks for a newer app.</summary>
    AppTooOld,
    SizeMismatch,
    HashMismatch,
    /// <summary>The bytes don't load with the regular reader.</summary>
    Unreadable,
    /// <summary>The pack's header doesn't match its manifest.</summary>
    ManifestMismatch,
    /// <summary>More than 5% fewer emoji than the data in use: treated as a broken upstream build.</summary>
    CountDrop,
}

/// <summary>Decisions about emoji data packs, kept free of I/O so they can be tested directly.</summary>
public static class EmojiPack
{
    /// <summary>Largest tolerated drop in emoji count between the data in use and a new pack.</summary>
    public const double MaxCountDrop = 0.05;

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether the manifest is worth downloading the pack for.</summary>
    public static PackVerdict CheckManifest(EmojiPackManifest manifest, int currentDataVersion, Version appVersion)
    {
        if (manifest.FormatVersion > EmojiData.SupportedFormatVersion)
            return PackVerdict.FormatTooNew;
        if (!Version.TryParse(manifest.MinAppVersion, out var min) || min > appVersion)
            return PackVerdict.AppTooOld;
        return manifest.DataVersion > currentDataVersion ? PackVerdict.Accept : PackVerdict.NotNewer;
    }

    /// <summary>
    /// Verifies a downloaded pack against its manifest and the data in use: size, hash, header, that it loads with
    /// the same reader, and that it doesn't lose more than <see cref="MaxCountDrop"/> of the emoji.
    /// </summary>
    public static PackVerdict CheckPack(ReadOnlySpan<byte> pack, EmojiPackManifest manifest, int currentEmojiCount)
    {
        if (pack.Length != manifest.Size)
            return PackVerdict.SizeMismatch;
        if (!string.Equals(Hash(pack), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return PackVerdict.HashMismatch;
        if (!EmojiData.TryReadHeader(pack, out var format, out var version))
            return PackVerdict.Unreadable;
        if (format > EmojiData.SupportedFormatVersion)
            return PackVerdict.FormatTooNew;
        if (format != manifest.FormatVersion || version != manifest.DataVersion)
            return PackVerdict.ManifestMismatch;

        int count;
        try
        {
            using var data = EmojiData.FromBytes(pack);
            count = data.Count;
        }
        catch (InvalidDataException)
        {
            return PackVerdict.Unreadable;
        }

        return count < currentEmojiCount * (1 - MaxCountDrop) ? PackVerdict.CountDrop : PackVerdict.Accept;
    }

    /// <summary>
    /// At startup: the downloaded pack when it is readable and newer than the embedded data, otherwise null (use the
    /// embedded data). <paramref name="corrupt"/> is set when the download should be deleted.
    /// </summary>
    public static EmojiData? ChooseDownloaded(ReadOnlySpan<byte> downloaded, int embeddedDataVersion, out bool corrupt)
    {
        corrupt = false;
        if (!EmojiData.TryReadHeader(downloaded, out var format, out var version))
        {
            corrupt = true;
            return null;
        }

        // An older format after an app update, or data older than what the new app embeds: not corrupt, just stale.
        if (format != EmojiData.SupportedFormatVersion || version <= embeddedDataVersion)
            return null;

        try
        {
            return EmojiData.FromBytes(downloaded);
        }
        catch (InvalidDataException)
        {
            corrupt = true;
            return null;
        }
    }
}
