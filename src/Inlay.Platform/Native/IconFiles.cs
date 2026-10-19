using System.Runtime.InteropServices;

namespace Inlay.Platform.Native;

// Loading the tray icon from an .ico file at an exact size. Common Controls v6 (app.manifest) provides
// LoadIconWithScaleDown.

internal static partial class Comctl32
{
    /// <summary>
    /// Loads an icon at exactly <paramref name="cx"/> × <paramref name="cy"/>, scaling a larger image down rather
    /// than a smaller one up. With <paramref name="hinst"/> 0, <paramref name="pszName"/> is the path of an .ico file.
    /// </summary>
    /// <remarks>
    /// Fails (ERROR_RESOURCE_TYPE_NOT_FOUND) for a file when the size is the system's large-icon size: it then
    /// looks for a resource instead. <see cref="User32.LoadImageW"/> handles that size.
    /// </remarks>
    [LibraryImport("comctl32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LoadIconWithScaleDown(nint hinst, string pszName, int cx, int cy, out nint phico);
}

internal static partial class User32
{
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);
}
