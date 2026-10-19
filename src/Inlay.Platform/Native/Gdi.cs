using System.Runtime.InteropServices;

namespace Inlay.Platform.Native;

// Icon extraction for the Excluded apps lists (Settings). Hand-written, source-generated marshalling.

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    public int fIcon;
    public int xHotspot;
    public int yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public short biPlanes;
    public short biBitCount;
    public int biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public int biClrUsed;
    public int biClrImportant;
}

internal static partial class Gdi32
{
    public const uint DIB_RGB_COLORS = 0;

    [LibraryImport("gdi32.dll")]
    public static unsafe partial int GetDIBits(nint hdc, nint hbm, uint start, uint lines, void* bits, BITMAPINFOHEADER* bmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);
}

internal static partial class IconApi
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    /// <summary>Extracts icon <paramref name="iIndex"/> of a file at <paramref name="nIconSize"/> (low word) pixels.</summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out nint phiconLarge, nint phiconSmall, uint nIconSize);
}
