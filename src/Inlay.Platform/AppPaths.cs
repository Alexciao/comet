namespace Inlay.Platform;

/// <summary>Where Inlay keeps its files, and where it lives on the web.</summary>
internal static class AppPaths
{
    /// <summary>A file with this name next to <c>Inlay.exe</c> makes it portable: data lives in <c>Data</c> beside it.</summary>
    public const string PortableMarker = "portable";

    /// <summary>The GitHub repository (owner/name): releases, the emoji data pack, the About link.</summary>
    public const string Repository = "Alexciao/inlay";

    private static string? s_dataDirectory;

    /// <summary>
    /// <c>%LocalAppData%\Inlay</c>; <c>Data</c> next to the executable in portable mode; <c>INLAY_DATA_DIR</c> when
    /// set (tests run against a scratch folder).
    /// </summary>
    public static string DataDirectory => s_dataDirectory ??= IsScratch
        ? Environment.GetEnvironmentVariable("INLAY_DATA_DIR")!
        : UsesPortableData
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : InstalledDataDirectory;

    /// <summary><c>%LocalAppData%\Inlay</c>.</summary>
    private static string InstalledDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inlay");

    /// <summary>Comet 1.0's data folder, <c>%LocalAppData%\Comet</c>.</summary>
    public static string CometDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CometLegacy.FolderName);

    private static bool? s_usesPortableData;

    /// <summary>Portable, with a writable <c>Data</c> folder next to the executable (a Comet 1.0 one is used as it is).</summary>
    private static bool UsesPortableData => s_usesPortableData ??= IsPortable && Writable(Path.Combine(AppContext.BaseDirectory, "Data"));

    /// <summary>
    /// First run after Comet 1.0: moves its data folder to Inlay's (see <see cref="CometLegacy.MigrateDataFolder"/>).
    /// Call before anything reads or writes the data folder. When the move fails, Comet's folder is used as it is
    /// for this run and the move is tried again next time.
    /// </summary>
    public static (CometLegacy.MigrationOutcome Outcome, string? Error) TakeOverCometData()
    {
        if (IsScratch || UsesPortableData)
            return (CometLegacy.MigrationOutcome.NothingToDo, null);
        var result = CometLegacy.MigrateDataFolder(CometDataDirectory, InstalledDataDirectory);
        if (result.Outcome == CometLegacy.MigrationOutcome.Failed)
            s_dataDirectory = CometDataDirectory;
        return result;
    }

    /// <summary>A portable copy unzipped somewhere read-only (Program Files, a locked share) keeps its data in the usual place.</summary>
    private static bool Writable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Running against a scratch data folder (tests, measurements): leave per-user machine state alone.</summary>
    public static bool IsScratch => Environment.GetEnvironmentVariable("INLAY_DATA_DIR") is { Length: > 0 };

    /// <summary>The portable zip ships a <see cref="PortableMarker"/> file next to the executable.</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string Version { get; } =
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static Uri RepositoryUrl { get; } = new($"https://github.com/{Repository}");

    public static Uri LicenseUrl { get; } = new($"https://github.com/{Repository}/blob/main/LICENSE");

    /// <summary>GitHub's API for the newest published (non-prerelease, non-draft) release.</summary>
    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>The fixed pre-release that carries the emoji data pack and its manifest.</summary>
    public static Uri EmojiDataEndpoint { get; } = new($"https://github.com/{Repository}/releases/download/emoji-data/");
}
