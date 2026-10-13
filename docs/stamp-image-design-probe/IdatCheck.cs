// Design probe only: stream IDAT through ZLibStream and check scanline structure.
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
foreach (var path in args.OrderBy(p => p))
{
    var sw = Stopwatch.StartNew();
    string result;
    try { result = Check(File.ReadAllBytes(path)); }
    catch (InvalidDataException e) { result = "InvalidDataException: " + e.Message; }
    Console.WriteLine($"{Path.GetFileName(path),-24} {result} ({sw.ElapsedMilliseconds} ms)");
}
static string Check(byte[] png)
{
    int pos = 8, w = 0, h = 0, depth = 0, color = 0; bool interlace = false;
    var idat = new MemoryStream();
    while (pos < png.Length)
    {
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos)); var kind = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
        var data = png.AsSpan(pos + 8, len);
        if (kind == "IHDR") { w = (int)BinaryPrimitives.ReadUInt32BigEndian(data); h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]); depth = data[8]; color = data[9]; interlace = data[12] == 1; }
        if (kind == "IDAT") idat.Write(data);
        pos += 12 + len;
    }
    idat.Position = 0;
    var channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
    (int x0, int y0, int dx, int dy)[] passes = interlace
        ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
        : [(0, 0, 1, 1)];
    using var z = new ZLibStream(idat, CompressionMode.Decompress);
    var row = new byte[(long)w * channels * depth / 8 + 2];
    long total = 0;
    foreach (var (x0, y0, dx, dy) in passes)
    {
        long pw = w > x0 ? (w - x0 + dx - 1) / dx : 0, ph = h > y0 ? (h - y0 + dy - 1) / dy : 0;
        if (pw == 0 || ph == 0) continue;
        var rowBytes = (int)(1 + (pw * channels * depth + 7) / 8);
        for (var y = 0; y < ph; y++)
        {
            z.ReadExactly(row, 0, rowBytes);
            if (row[0] > 4) return "bad filter type";
            total += rowBytes;
        }
    }
    var extra = z.Read(row, 0, 1);
    return extra != 0 ? "too much data" : $"ok inflated={total}";
}
