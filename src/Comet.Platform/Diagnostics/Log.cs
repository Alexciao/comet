using System.Globalization;
using System.Text;

namespace Comet.Platform;

/// <summary>
/// Local log in <c>%LocalAppData%\Comet\logs</c>. Records events and timings only — never typed text. Lines are
/// queued and written by a background thread, so logging never does disk I/O on the caller's thread (some
/// callers are inside the keyboard hook, which Windows drops if it is slow). Rotated at 1 MB (one old copy kept).
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1 << 20;
    private const int MaxCrashReports = 10;
    private static readonly Lock s_lock = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> s_pending = new();
    private static readonly AutoResetEvent s_signal = new(false);
    private static int s_writerStarted;
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
        s_pending.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}"));
        if (Interlocked.Exchange(ref s_writerStarted, 1) == 0)
        {
            new Thread(WriterLoop) { IsBackground = true, Name = "Comet log", Priority = ThreadPriority.BelowNormal }.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }

        s_signal.Set();
    }

    private static void WriterLoop()
    {
        while (true)
        {
            s_signal.WaitOne();
            Drain();
        }
    }

    /// <summary>Writes everything queued so far; used on exit and before a crash report.</summary>
    public static void Flush() => Drain();

    private static void Drain()
    {
        lock (s_lock)
        {
            if (s_pending.IsEmpty)
                return;
            var text = new StringBuilder();
            while (s_pending.TryDequeue(out var line))
                text.Append(line);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
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
            // Keep the newest few: a crash loop must not fill the disk.
            foreach (var old in System.IO.Directory.GetFiles(Directory, "crash-*.log").OrderDescending(StringComparer.Ordinal).Skip(MaxCrashReports))
                File.Delete(old);
            Write("FATAL", $"{source}: {ex.GetType().Name}: {ex.Message} (see {Path.GetFileName(path)})");
            Flush(); // the process may end right after this
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

