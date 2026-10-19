using Inlay.Platform.Native;

namespace Inlay.App.UI;

/// <summary>Reads clipboard text on the UI thread (for the snippet preview).</summary>
internal static unsafe class ClipboardReader
{
    private const uint CF_UNICODETEXT = 13;

    public static string Text()
    {
        if (!User32.IsClipboardFormatAvailable(CF_UNICODETEXT) || !User32.OpenClipboard(0))
            return "";
        try
        {
            var handle = User32.GetClipboardData(CF_UNICODETEXT);
            var p = handle == 0 ? 0 : Kernel32.GlobalLock(handle);
            if (p == 0)
                return "";
            try
            {
                return new string((char*)p);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }
}
