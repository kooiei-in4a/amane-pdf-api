using System.Runtime.InteropServices;

namespace Amane.Pdf.Renderer;

// One library instance, document and page per process. Never loaded by the API.
internal sealed class Pdfium : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Config
    {
        internal int Version;
        internal IntPtr UserFontPaths;
        internal IntPtr Isolate;
        internal uint V8Slot;
        internal IntPtr Platform;
        internal int Renderer;
        internal IntPtr FontLibrary;
        internal int FontBackend;
    }

    private readonly IntPtr fontPath;
    private readonly IntPtr fontPaths;
    private bool initialized;

    internal Pdfium(string libraryPath, string fontDirectory)
    {
        if (!Path.IsPathFullyQualified(libraryPath)) throw new ArgumentException();
        NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, (name, _, _) =>
            name == "pdfium" ? NativeLibrary.Load(libraryPath) : IntPtr.Zero);
        fontPath = Marshal.StringToCoTaskMemUTF8(fontDirectory);
        try
        {
            fontPaths = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(fontPaths, 0, fontPath);
            Marshal.WriteIntPtr(fontPaths, IntPtr.Size, IntPtr.Zero);
            var config = new Config { Version = 2, UserFontPaths = fontPaths };
            FPDF_InitLibraryWithConfig(ref config);
            initialized = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (initialized) FPDF_DestroyLibrary();
        initialized = false;
        Marshal.FreeHGlobal(fontPaths);
        Marshal.FreeCoTaskMem(fontPath);
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_InitLibraryWithConfig(ref Config config);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_DestroyLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, IntPtr password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern nuint FPDF_GetLastError();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
}
