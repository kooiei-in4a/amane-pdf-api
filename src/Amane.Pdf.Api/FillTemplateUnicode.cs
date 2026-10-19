using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal static class FillTemplateUnicode
{
    internal static void WriteUpdate(Utf8JsonWriter writer, PdfPageBoxes metadata,
        FillTemplateFields fields, CancellationToken token)
    {
        var fonts = metadata.Objects.Values.Where(obj => obj.TryGetProperty("value", out var v) &&
            v.ValueKind == JsonValueKind.Object && v.TryGetProperty("/Subtype", out var subtype) &&
            subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "/Type0").ToArray();
        if (fonts.Length != 1) throw new InvalidOperationException(PdfOverlayBuilder.Failure);
        var font = fonts[0].GetProperty("value");
        var reference = PdfObjectRef.Parse(font.GetProperty("/ToUnicode")) ?? throw new InvalidOperationException(PdfOverlayBuilder.Failure);
        if (!metadata.Objects.TryGetValue(reference, out var obj) || !obj.TryGetProperty("stream", out var stream))
            throw new InvalidOperationException(PdfOverlayBuilder.Failure);
        writer.WriteStartObject(); writer.WriteStartArray("qpdf"); writer.WriteStartObject();
        writer.WriteNumber("jsonversion", 2); writer.WriteEndObject(); writer.WriteStartObject();
        writer.WriteStartObject(reference.Key); writer.WriteStartObject("stream"); writer.WriteStartObject("dict");
        foreach (var property in stream.GetProperty("dict").EnumerateObject())
            if (property.Name is not ("/Length" or "/Filter" or "/DecodeParms")) property.WriteTo(writer);
        writer.WriteEndObject();
        writer.WriteBase64String("data", Encoding.ASCII.GetBytes(CMap(fields, token)));
        writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static string CMap(FillTemplateFields fields, CancellationToken token)
    {
        var glyphs = new SortedDictionary<ushort, char>();
        foreach (var c in fields.Items.SelectMany(field => field.Text))
        {
            token.ThrowIfCancellationRequested();
            if (c == '\n') continue;
            if (!FillTemplateGlyphs.TryGet(c, out var gid, out _)) throw new InvalidOperationException(PdfOverlayBuilder.Failure);
            if (glyphs.TryGetValue(gid, out var previous) && previous != c) throw new InvalidOperationException(PdfOverlayBuilder.Failure);
            glyphs[gid] = c;
        }
        var result = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /AmaneFillTemplate def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var block in glyphs.Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            result.Append(block.Length.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (gid, c) in block)
                result.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(((ushort)c).ToString("X4", CultureInfo.InvariantCulture)).Append(">\n");
            result.Append("endbfchar\n");
        }
        return result.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n").ToString();
    }
}
