using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

// Spike only.
//   render <in.pdf> <page0> <wpx> <hpx> <rects "x,y,w,h;..."|-> <out.jpg> [fontdir|-] [repeat]
//   info <in.pdf> [fontdir|-]
static class P
{
    [StructLayout(LayoutKind.Sequential)]
    struct Config { public int Version; public IntPtr UserFontPaths; public IntPtr Isolate; public uint V8Slot; public IntPtr Platform; public int Renderer; }
    [DllImport("pdfium")] static extern void FPDF_InitLibraryWithConfig(ref Config c);
    [DllImport("pdfium")] static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr pw);
    [DllImport("pdfium")] static extern uint FPDF_GetLastError();
    [DllImport("pdfium")] static extern int FPDF_GetPageCount(IntPtr d);
    [DllImport("pdfium")] static extern IntPtr FPDF_LoadPage(IntPtr d, int i);
    [DllImport("pdfium")] static extern float FPDF_GetPageWidthF(IntPtr p);
    [DllImport("pdfium")] static extern float FPDF_GetPageHeightF(IntPtr p);
    [DllImport("pdfium")] static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport("pdfium")] static extern int FPDFBitmap_FillRect(IntPtr b, int l, int t, int w, int h, uint color);
    [DllImport("pdfium")] static extern void FPDF_RenderPageBitmap(IntPtr b, IntPtr p, int x, int y, int w, int h, int rot, int flags);
    [DllImport("pdfium")] static extern IntPtr FPDFBitmap_GetBuffer(IntPtr b);
    [DllImport("pdfium")] static extern int FPDFBitmap_GetStride(IntPtr b);
    [DllImport("pdfium")] static extern void FPDFBitmap_Destroy(IntPtr b);
    [DllImport("pdfium")] static extern void FPDF_ClosePage(IntPtr p);
    [DllImport("pdfium")] static extern void FPDF_CloseDocument(IntPtr d);
    [DllImport("libturbojpeg.so.0")] static extern IntPtr tjInitCompress();
    [DllImport("libturbojpeg.so.0")] static extern int tjCompress2(IntPtr h, IntPtr src, int w, int pitch, int hh, int pf, ref IntPtr dst, ref nuint size, int samp, int q, int flags);
    [DllImport("libturbojpeg.so.0")] static extern void tjFree(IntPtr p);
    [DllImport("libturbojpeg.so.0")] static extern int tjDestroy(IntPtr h);

    static int Main(string[] a)
    {
        var sw = Stopwatch.StartNew();
        var info = a[0] == "info";
        var fontdir = info ? (a.Length > 2 ? a[2] : null) : (a.Length > 7 ? a[7] : null);
        var paths = IntPtr.Zero;
        if (fontdir is not null && fontdir != "-")
        {
            paths = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(paths, 0, Marshal.StringToCoTaskMemUTF8(fontdir));
            Marshal.WriteIntPtr(paths, IntPtr.Size, IntPtr.Zero);
        }
        var cfg = new Config { Version = 2, UserFontPaths = paths };
        FPDF_InitLibraryWithConfig(ref cfg);
        var doc = FPDF_LoadDocument(a[1], IntPtr.Zero);
        if (doc == IntPtr.Zero) { Console.Error.WriteLine("load error " + FPDF_GetLastError()); return 3; }
        if (info)
        {
            var n = FPDF_GetPageCount(doc);
            for (var i = 0; i < n; i++)
            {
                var pg = FPDF_LoadPage(doc, i);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i} {FPDF_GetPageWidthF(pg)} {FPDF_GetPageHeightF(pg)}"));
                FPDF_ClosePage(pg);
            }
            return 0;
        }
        var index = int.Parse(a[2], CultureInfo.InvariantCulture);
        var w = int.Parse(a[3], CultureInfo.InvariantCulture);
        var h = int.Parse(a[4], CultureInfo.InvariantCulture);
        var repeat = a.Length > 8 ? int.Parse(a[8], CultureInfo.InvariantCulture) : 1;
        var page = FPDF_LoadPage(doc, index);
        if (page == IntPtr.Zero) { Console.Error.WriteLine("page error"); return 4; }
        var tLoad = sw.ElapsedMilliseconds;
        var bmp = IntPtr.Zero; long tRender = 0;
        for (var r = 0; r < repeat; r++)
        {
            if (bmp != IntPtr.Zero) FPDFBitmap_Destroy(bmp);
            bmp = FPDFBitmap_Create(w, h, 0);
            if (bmp == IntPtr.Zero) { Console.Error.WriteLine("bitmap error"); return 5; }
            FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            var t0 = sw.ElapsedMilliseconds;
            FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, 0);
            tRender += sw.ElapsedMilliseconds - t0;
        }
        if (a[5] != "-")
            foreach (var rect in a[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = rect.Split(',').Select(s => decimal.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                int L = (int)Math.Floor(v[0] * w), T = (int)Math.Floor(v[1] * h);
                int R = (int)Math.Ceiling((v[0] + v[2]) * w), B = (int)Math.Ceiling((v[1] + v[3]) * h);
                FPDFBitmap_FillRect(bmp, L, T, R - L, B - T, 0xFF000000);
            }
        var t1 = sw.ElapsedMilliseconds;
        var tj = tjInitCompress(); var jpg = IntPtr.Zero; nuint size = 0;
        if (tjCompress2(tj, FPDFBitmap_GetBuffer(bmp), w, FPDFBitmap_GetStride(bmp), h, 3 /*TJPF_BGRX*/, ref jpg, ref size, 2 /*TJSAMP_420*/, 85, 0) != 0)
        { Console.Error.WriteLine("jpeg error"); return 6; }
        unsafe
        {
            using var f = File.Create(a[6]);
            f.Write(new ReadOnlySpan<byte>((void*)jpg, checked((int)size)));
        }
        var tEnc = sw.ElapsedMilliseconds - t1;
        tjFree(jpg); tjDestroy(tj);
        FPDFBitmap_Destroy(bmp); FPDF_ClosePage(page); FPDF_CloseDocument(doc);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"load={tLoad}ms render={tRender / repeat}ms encode={tEnc}ms total={sw.ElapsedMilliseconds}ms jpeg={size}"));
        return 0;
    }
}
