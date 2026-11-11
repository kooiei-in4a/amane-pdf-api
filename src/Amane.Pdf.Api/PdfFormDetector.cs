using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal static class PdfFormDetector
{
    internal static void RejectForms(PdfPageBoxes metadata, CancellationToken token)
    {
        if (ContainsRestrictedStructure(metadata, includeForms: true, token))
            throw new PdfFillTemplateException(PdfFillTemplateReason.FormFields);
    }

    internal static bool ContainsSignatureStructure(PdfPageBoxes metadata, CancellationToken token)
        => ContainsRestrictedStructure(metadata, includeForms: false, token);

    private static bool ContainsRestrictedStructure(PdfPageBoxes metadata, bool includeForms, CancellationToken token)
    {
        // Follow the trailer's Root rather than treating every Catalog-like dictionary as the root.
        var trailer = metadata.Document.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value");
        var root = metadata.Resolve(trailer.GetProperty("/Root"), token);
        foreach (var property in root.EnumerateObject())
        {
            var key = Name(property.Name);
            if (key == "/Perms" || (includeForms && key == "/AcroForm")) return true;
        }
        // References need not be followed: each indirect object is visited exactly once.
        // The bounded DOM also contains object stream members and stream dictionaries.
        return metadata.Objects.Values.Any(obj => Scan(obj, includeForms, token));
    }

    private static bool Scan(JsonElement value, bool includeForms, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var key = Name(property.Name);
                    var name = Name(property.Value.GetString()!);
                    if ((includeForms && key == "/Subtype" && name == "/Widget") ||
                        (key == "/FT" && name == "/Sig") ||
                        (key == "/Type" && name is "/Sig" or "/DocTimeStamp")) return true;
                }
                if (Scan(property.Value, includeForms, token)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (Scan(item, includeForms, token)) return true;
        }
        return false;
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
}
