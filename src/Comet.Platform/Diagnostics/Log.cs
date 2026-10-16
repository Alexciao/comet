using System.Globalization;
using System.Text;

namespace Comet.Platform;

/// <summary>
/// Local log in <c>%LocalAppData%\Comet\logs</c>. Records events and timings only — never typed text.
/// Writes are rare (start/stop, failures, timing summaries) so a lock and a direct append are enough.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock s_lock = new();
    private static string? s_directory;

    public static string Directory => s_directory ??= Path.Combine(AppPaths.DataDirectory, "logs");

    public static string FilePath => Path.Combine(Directory, "comet.log");

    /// <summary>Redirects logging (tests).</summary>
    public static void UseDirectory(string directory) => s_directory = directory;

    public static void Info(string message) => Write("INFO", message);

    /// <summary>Diagnostics compiled into debug builds only.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Debug(string message) => Write("DEBUG", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            lock (s_lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes a crash report next to the log and returns its path.</summary>
    public static string? WriteCrash(Exception ex, string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, string.Create(CultureInfo.InvariantCulture, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            File.WriteAllText(path, $"Comet {AppPaths.Version} crashed ({source}) at {DateTime.Now:O}\n{Environment.OSVersion}\n\n{ex}\n", Encoding.UTF8);
            Write("FATAL", $"{source}: {ex.GetType().Name}: {ex.Message} (see {Path.GetFileName(path)})");
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Where Comet keeps its files.</summary>
internal static class AppPaths
{
    private static string? s_dataDirectory;

    /// <summary><c>%LocalAppData%\Comet</c>, or <c>COMET_DATA_DIR</c> when set (tests run against a scratch folder).</summary>
    public static string DataDirectory => s_dataDirectory ??= IsScratch
        ? Environment.GetEnvironmentVariable("COMET_DATA_DIR")!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Comet");

    /// <summary>Running against a scratch data folder (tests, measurements): leave per-user machine state alone.</summary>
    public static bool IsScratch => Environment.GetEnvironmentVariable("COMET_DATA_DIR") is { Length: > 0 };

    public static string Version { get; } =
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
