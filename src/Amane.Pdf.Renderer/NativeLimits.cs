using System.Runtime.InteropServices;

namespace Amane.Pdf.Renderer;

internal static class NativeLimits
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Limit { internal ulong Current; internal ulong Maximum; }

    // CoreCLR's W^X code heap uses a memfd while bootstrapping. Lower both limits
    // after bootstrap, before loading PDFium or opening any untrusted document.
    internal static bool SetFileSize(long bytes)
    {
        var limit = new Limit { Current = (ulong)bytes, Maximum = (ulong)bytes };
        return setrlimit(1 /* RLIMIT_FSIZE, Linux */, ref limit) == 0;
    }

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int setrlimit(int resource, ref Limit limit);
}
