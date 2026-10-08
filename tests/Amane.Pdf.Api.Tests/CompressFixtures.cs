using System.Text;

namespace Amane.Pdf.Api.Tests;

internal static class CompressFixtures
{
    internal static async Task<byte[]> JpegAsync(PdfTestContext test, int width = 2200, int height = 1600,
        bool gray = false, bool progressive = false, bool sample444 = false)
    {
        var pnm = Path.Combine(test.Root, "source.pnm"); var jpeg = Path.Combine(test.Root, "source.jpg");
        await using (var file = File.Create(pnm))
        {
            await file.WriteAsync(Encoding.ASCII.GetBytes($"P{(gray ? 5 : 6)}\n{width} {height}\n255\n"));
            var row = new byte[width * (gray ? 1 : 3)];
            var random = new Random(22);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < row.Length; x++) row[x] = (byte)((x / 20 + y / 15 + random.Next(64)) % 256);
                await file.WriteAsync(row);
            }
        }
        var args = new List<string> { "-quality", "90", "-outfile", jpeg };
        if (progressive) args.Add("-progressive");
        if (sample444) args.AddRange(["-sample", "1x1"]);
        args.Add(pnm);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.AreEqual(0, (await ExternalProcessRunner.RunAsync(new("cjpeg", args), timeout.Token)).ExitCode);
        return await File.ReadAllBytesAsync(jpeg);
    }

    internal static byte[] Pdf(byte[] jpeg, int width = 2200, int height = 1600, bool gray = false,
        string extra = "", string? color = null, int pages = 1, string filter = "/DCTDecode", byte[][]? extras = null)
    {
        var contents = Encoding.ASCII.GetBytes("q 595 0 0 842 0 0 cm /Im0 Do Q\n0 0 1 RG 10 10 100 100 re S\nBT /F1 18 Tf 20 200 Td (Compress text) Tj ET\n");
        var objects = new List<byte[]>
        {
            Text("<< /Type /Catalog /Pages 2 0 R >>"),
            Text($"<< /Type /Pages /Kids [3 0 R {(pages == 2 ? "7 0 R" : "")}] /Count {pages} >>"),
            Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im0 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>"),
            Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {color ?? (gray ? "/DeviceGray" : "/DeviceRGB")} /BitsPerComponent 8 /Filter {filter} {extra}", jpeg),
            Stream("", contents),
            Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };
        if (pages == 2) objects.Add(objects[2]);
        if (extras is not null) objects.AddRange(extras);
        return Objects(objects);
    }

    internal static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);
    internal static byte[] Many(byte[] jpeg, int count)
    {
        var objects = new List<byte[]> { Text("<< /Type /Catalog /Pages 2 0 R >>"),
            Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), Array.Empty<byte>() };
        var resources = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            resources.Append($"/Im{i} {i + 4} 0 R ");
            objects.Add(Stream("/Type /XObject /Subtype /Image /Width 2200 /Height 1600 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));
        }
        objects[2] = Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << {resources} >> >> >>");
        return Objects(objects);
    }
    internal static byte[] Stream(string dictionary, byte[] data)
        => [.. Text($"<< {dictionary} /Length {data.Length} >>\nstream\n"), .. data, .. Text("\nendstream")];
    internal static byte[] Objects(List<byte[]> objects)
    {
        using var output = new MemoryStream();
        output.Write(Text("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Text($"{i + 1} 0 obj\n")); output.Write(objects[i]); output.Write(Text("\nendobj\n"));
        }
        var xref = output.Position;
        output.Write(Text($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets) output.Write(Text($"{offset:0000000000} 00000 n \n"));
        output.Write(Text($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }
}
