using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal static class PdfFormDetector
{
    internal static void RejectForms(PdfPageBoxes metadata, CancellationToken token)
    {
        // Follow the trailer's Root rather than treating every Catalog-like dictionary as the root.
        var trailer = metadata.Document.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value");
        var root = metadata.Resolve(trailer.GetProperty("/Root"), token);
        foreach (var property in root.EnumerateObject())
            if (Name(property.Name) is "/AcroForm" or "/Perms") Reject();
        // References need not be followed: each indirect object is visited exactly once.
        // The bounded DOM also contains object stream members and stream dictionaries.
        foreach (var obj in metadata.Objects.Values) Scan(obj, token);
    }

    private static void Scan(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var key = Name(property.Name);
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var name = Name(property.Value.GetString()!);
                    if ((key == "/Subtype" && name == "/Widget") || (key == "/FT" && name == "/Sig") ||
                        (key == "/Type" && name is "/Sig" or "/DocTimeStamp")) Reject();
                }
                Scan(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Scan(item, token);
    }

    private static string Name(string name)
    {
        if (!name.Contains('#')) return name;
        var decoded = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '#' && i + 2 < name.Length && byte.TryParse(name.AsSpan(i + 1, 2),
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
            { decoded.Append((char)b); i += 2; }
            else decoded.Append(name[i]);
        }
        return decoded.ToString();
    }

    private static void Reject() => throw new PdfFillTemplateException(PdfFillTemplateReason.FormFields);
}
