using System.Security.Cryptography;
using Inlay.Platform.Native;

namespace Inlay.Platform;

/// <summary>
/// Inlay 1.1 was called Comet in 1.0. On first run Inlay takes over from it: a running <c>Comet.exe</c> is asked
/// to quit, <c>%LocalAppData%\Comet</c> moves to <c>%LocalAppData%\Inlay</c>, and the <c>Comet</c> start-at-login
/// entry is replaced by Inlay's. These names are the only places Comet still appears in the code.
/// </summary>
internal static class CometLegacy
{
    public const string FolderName = "Comet";

    public const string RunValueName = "Comet";

    /// <summary>Comet 1.0's hidden message window; a running Comet is found (and asked to quit) through it.</summary>
    public const string MessageWindowClass = "Comet.MessageWindow";

    /// <summary>Comet 1.0's "quit cleanly" message (the one its installer sends).</summary>
    private const uint WmQuit = User32.WM_APP + 101;

    /// <summary>What the old folder becomes once its copy is in place, until Inlay has read its data from the copy.</summary>
    public const string BackupSuffix = ".backup";

    /// <summary>
    /// Asks a running Comet 1.0 to quit (it unhooks and saves, exactly as on <c>Comet.exe --quit</c>) and waits up
    /// to 5 s, so two apps never hook the keyboard at once and its files are complete before they move.
    /// </summary>
    public static bool QuitRunningComet()
    {
        var window = User32.FindWindowW(MessageWindowClass, null);
        if (window == 0)
            return false;
        User32.GetWindowThreadProcessId(window, out var pid);
        User32.PostMessageW(window, WmQuit, 0, 0);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // already gone
        }

        return true;
    }

    public enum MigrationOutcome
    {
        /// <summary>No Comet folder.</summary>
        NothingToDo,
        /// <summary>Inlay already has data: Comet's folder is left alone.</summary>
        AlreadyMigrated,
        /// <summary>Copied, verified and switched over; the old folder is kept as a backup for now.</summary>
        Migrated,
        /// <summary>The copy couldn't be made or didn't match: Comet's folder is used as it is, and the move is tried again next time.</summary>
        Failed,
    }

    /// <summary>
    /// Moves Comet's data folder to Inlay's. The data is copied to a staging folder first and every file is compared
    /// (size and SHA-256) before the staging folder becomes the Inlay folder; only then is the old folder renamed to
    /// <c>Comet.backup</c>. A copy that fails or doesn't match is removed and nothing else changes. The backup is
    /// deleted by <see cref="DeleteBackup"/> once Inlay has read its settings from the new folder.
    /// </summary>
    public static (MigrationOutcome Outcome, string? Error) MigrateDataFolder(string cometDirectory, string inlayDirectory)
    {
        if (!Directory.Exists(cometDirectory))
            return (MigrationOutcome.NothingToDo, null);
        if (Directory.Exists(inlayDirectory) && Directory.EnumerateFileSystemEntries(inlayDirectory).Any())
            return (MigrationOutcome.AlreadyMigrated, null);

        var staging = inlayDirectory + ".migrating";
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Copy(cometDirectory, staging);
            if (!SameContent(cometDirectory, staging))
            {
                Directory.Delete(staging, recursive: true);
                return (MigrationOutcome.Failed, "the copy didn't match the original");
            }

            if (Directory.Exists(inlayDirectory))
                Directory.Delete(inlayDirectory); // empty (checked above)
            Directory.Move(staging, inlayDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return (MigrationOutcome.Failed, ex.Message);
        }

        try
        {
            var backup = cometDirectory + BackupSuffix;
            TryDelete(backup);
            Directory.Move(cometDirectory, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data is safely in the Inlay folder; the old one just stays where it was.
            return (MigrationOutcome.Migrated, "the old folder couldn't be renamed: " + ex.Message);
        }

        return (MigrationOutcome.Migrated, null);
    }

    /// <summary>Removes <c>Comet.backup</c> (call once the data has been read back from the Inlay folder).</summary>
    public static bool DeleteBackup(string cometDirectory)
    {
        var backup = cometDirectory + BackupSuffix;
        if (!Directory.Exists(backup))
            return false;
        TryDelete(backup);
        return !Directory.Exists(backup);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static bool SameContent(string a, string b)
    {
        var filesA = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var filesB = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!filesA.SequenceEqual(filesB, StringComparer.OrdinalIgnoreCase))
            return false;
        foreach (var relative in filesA)
        {
            var fa = new FileInfo(Path.Combine(a, relative));
            var fb = new FileInfo(Path.Combine(b, relative));
            if (fa.Length != fb.Length || !Hash(fa.FullName).AsSpan().SequenceEqual(Hash(fb.FullName)))
                return false;
        }

        return true;
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for next time
        }
    }
}
