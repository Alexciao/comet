using System.Runtime.CompilerServices;

namespace Inlay.Platform.Tests;

internal static class TestSetup
{
    /// <summary>Code under test logs; keep that out of the user's real %LocalAppData%\Inlay.</summary>
    [ModuleInitializer]
    internal static void RedirectLog() =>
        Log.UseDirectory(Path.Combine(Path.GetTempPath(), "inlay-platform-tests", "logs"));
}
