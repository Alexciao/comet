using System.Runtime.CompilerServices;

namespace Comet.Platform.Tests;

internal static class TestSetup
{
    /// <summary>Code under test logs; keep that out of the user's real %LocalAppData%\Comet.</summary>
    [ModuleInitializer]
    internal static void RedirectLog() =>
        Log.UseDirectory(Path.Combine(Path.GetTempPath(), "comet-platform-tests", "logs"));
}
