using System.Runtime.InteropServices;

namespace Inlay.Platform.Native;

// Images: known folders, shell thumbnails, WIC decoding, clipboard formats. Hand-written, source-generated marshalling.

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

internal static partial class ImageApi
{
    /// <summary>Known folders that may not exist yet (Screenshots before the first Win+PrtScn) still return their path.</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    public const uint CF_DIB = 8;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    public const uint COINIT_APARTMENTTHREADED = 2;

    public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);

    /// <summary>Converts a WIC bitmap source to another pixel format (returns the source itself, AddRef'd, when it already matches).</summary>
    [LibraryImport("windowscodecs.dll")]
    public static partial int WICConvertBitmapSource(in Guid dstFormat, nint pISrc, out nint ppIDst);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);
}
