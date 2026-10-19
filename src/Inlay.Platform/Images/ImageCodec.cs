using Inlay.Platform.Native;

namespace Inlay.Platform.Images;

/// <summary>
/// Decodes an image file to pixels with the Windows Imaging Component, through raw COM vtables (AOT-safe, like
/// <c>Uia</c>). Slot numbers come from wincodec.h (IUnknown occupies 0–2). Used only to put a bitmap on the
/// clipboard when an app asks for one, never for thumbnails.
/// </summary>
internal static unsafe class ImageCodec
{
    private static readonly Guid ClsidWicImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid PixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    private const uint GENERIC_READ = 0x80000000;
    private const int WICDecodeMetadataCacheOnDemand = 0;

    private const int SlotCreateDecoderFromFilename = 3; // IWICImagingFactory
    private const int SlotGetFrame = 13;                 // IWICBitmapDecoder
    private const int SlotGetSize = 3;                   // IWICBitmapSource
    private const int SlotCopyPixels = 7;

    private static nint* Vtbl(nint obj) => *(nint**)obj;

    internal static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(obj)[2])(obj);
    }

    /// <summary>The first frame of an image file as straight-alpha BGRA, or null (unreadable, unsupported, or too big).</summary>
    public static BgraImage? Decode(string path)
    {
        // Whatever apartment the thread is in works for WIC; initialise COM only when nobody has.
        var init = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        nint factory = 0, decoder = 0, frame = 0, bgra = 0;
        try
        {
            if (Ole32.CoCreateInstance(ClsidWicImagingFactory, 0, Ole32.CLSCTX_INPROC_SERVER, IidWicImagingFactory, out factory) < 0)
                return null;
            fixed (char* name = path)
            {
                if (((delegate* unmanaged[Stdcall]<nint, char*, Guid*, uint, int, nint*, int>)Vtbl(factory)[SlotCreateDecoderFromFilename])(factory, name, null, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder) < 0)
                    return null;
            }

            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(decoder)[SlotGetFrame])(decoder, 0, &frame) < 0)
                return null;
            if (ImageApi.WICConvertBitmapSource(PixelFormat32bppBgra, frame, out bgra) < 0)
                return null;

            uint width = 0, height = 0;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Vtbl(bgra)[SlotGetSize])(bgra, &width, &height) < 0
                || width == 0 || height == 0 || (long)width * height > Dib.MaxPixels)
                return null;

            var pixels = new byte[checked((int)(width * height * 4))];
            fixed (byte* p = pixels)
            {
                if (((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Vtbl(bgra)[SlotCopyPixels])(bgra, null, width * 4, (uint)pixels.Length, p) < 0)
                    return null;
            }

            return new BgraImage((int)width, (int)height, pixels);
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            Release(bgra);
            Release(frame);
            Release(decoder);
            Release(factory);
            if (init >= 0)
                Ole32.CoUninitialize();
        }
    }
}
