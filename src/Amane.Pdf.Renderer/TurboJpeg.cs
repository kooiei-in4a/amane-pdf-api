using System.Runtime.InteropServices;

namespace Amane.Pdf.Renderer;

internal sealed class TurboJpeg : IDisposable
{
    private IntPtr compressor;
    private IntPtr buffer;
    private nuint size;

    internal bool Compress(IntPtr bitmap, int width, int height)
    {
        compressor = tjInitCompress();
        return compressor != IntPtr.Zero && tjCompress2(compressor, Pdfium.FPDFBitmap_GetBuffer(bitmap),
            width, Pdfium.FPDFBitmap_GetStride(bitmap), height, 3 /* TJPF_BGRX */, ref buffer, ref size,
            2 /* TJSAMP_420 */, 85, 0 /* baseline */) == 0 && buffer != IntPtr.Zero && size > 0;
    }

    internal nuint Size => size;
    internal unsafe ReadOnlySpan<byte> Bytes => new((void*)buffer, checked((int)size));

    public void Dispose()
    {
        if (buffer != IntPtr.Zero) tjFree(buffer);
        if (compressor != IntPtr.Zero) tjDestroy(compressor);
        buffer = compressor = IntPtr.Zero;
    }

    [DllImport("libturbojpeg.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr tjInitCompress();
    // unsigned long is pointer-sized on the supported Linux x86_64 runtime.
    [DllImport("libturbojpeg.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int tjCompress2(IntPtr handle, IntPtr source, int width, int pitch, int height, int format, ref IntPtr destination, ref nuint size, int sampling, int quality, int flags);
    [DllImport("libturbojpeg.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern void tjFree(IntPtr buffer);
    [DllImport("libturbojpeg.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int tjDestroy(IntPtr handle);
}
