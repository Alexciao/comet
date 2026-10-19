namespace Comet.Platform;

/// <summary>Where Comet keeps its files, and where it lives on the web.</summary>
internal static class AppPaths
{
    /// <summary>A file with this name next to <c>Comet.exe</c> makes it portable: data lives in <c>Data</c> beside it.</summary>
    public const string PortableMarker = "portable";

    /// <summary>The GitHub repository (owner/name): releases, the emoji data pack, the About link.</summary>
    public const string Repository = "Alexciao/comet";

    private static string? s_dataDirectory;

    /// <summary>
    /// <c>%LocalAppData%\Comet</c>; <c>Data</c> next to the executable in portable mode; <c>COMET_DATA_DIR</c> when
    /// set (tests run against a scratch folder).
    /// </summary>
    public static string DataDirectory => s_dataDirectory ??= IsScratch
        ? Environment.GetEnvironmentVariable("COMET_DATA_DIR")!
        : IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Comet");

    /// <summary>Running against a scratch data folder (tests, measurements): leave per-user machine state alone.</summary>
    public static bool IsScratch => Environment.GetEnvironmentVariable("COMET_DATA_DIR") is { Length: > 0 };

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
