using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal static class PdfFormDetector
{
    private enum Inspection { Forms, SignatureStructures, AppliedSignatures }

    internal static void RejectForms(PdfPageBoxes metadata, CancellationToken token)
    {
        if (ContainsRestrictedStructure(metadata, Inspection.Forms, token))
            throw new PdfFillTemplateException(PdfFillTemplateReason.FormFields);
    }

    internal static bool ContainsSignatureStructure(PdfPageBoxes metadata, CancellationToken token)
        => ContainsRestrictedStructure(metadata, Inspection.SignatureStructures, token);

    internal static bool ContainsAppliedSignature(PdfPageBoxes metadata, CancellationToken token)
        => ContainsRestrictedStructure(metadata, Inspection.AppliedSignatures, token);

    private static bool ContainsRestrictedStructure(PdfPageBoxes metadata, Inspection inspection, CancellationToken token)
    {
        // Follow the trailer's Root rather than treating every Catalog-like dictionary as the root.
        if (inspection != Inspection.AppliedSignatures)
        {
            foreach (var property in metadata.Catalog(token).EnumerateObject())
            {
                var key = Name(property.Name);
                if (key == "/Perms" || (inspection == Inspection.Forms && key == "/AcroForm")) return true;
            }
        }
        // References need not be followed: each indirect object is visited exactly once.
        // The bounded DOM also contains object stream members and stream dictionaries.
        return metadata.Objects.Values.Any(obj => Scan(obj, inspection, token));
    }

    private static bool Scan(JsonElement value, Inspection inspection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            JsonElement filter = default, contents = default, byteRange = default;
            foreach (var property in value.EnumerateObject())
            {
                var key = Name(property.Name);
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    if (key == "/Type" && Name(property.Value.GetString()!) is "/Sig" or "/DocTimeStamp") return true;
                    if (inspection != Inspection.AppliedSignatures && key == "/FT" &&
                        Name(property.Value.GetString()!) == "/Sig") return true;
                    if (inspection == Inspection.Forms && key == "/Subtype" &&
                        Name(property.Value.GetString()!) == "/Widget") return true;
                }
                if (inspection == Inspection.AppliedSignatures)
                {
                    switch (key)
                    {
                        case "/Filter": filter = property.Value; break;
                        case "/Contents": contents = property.Value; break;
                        case "/ByteRange": byteRange = property.Value; break;
                    }
                }
                if (Scan(property.Value, inspection, token)) return true;
            }

            // qpdf JSON v2 represents PDF names with '/', and PDF strings with 'b:' or 'u:'.
            if (byteRange.ValueKind == JsonValueKind.Array &&
                filter.ValueKind == JsonValueKind.String && filter.GetString()!.StartsWith('/') &&
                contents.ValueKind == JsonValueKind.String && contents.GetString() is string pdfString &&
                (pdfString.StartsWith("b:", StringComparison.Ordinal) || pdfString.StartsWith("u:", StringComparison.Ordinal)))
            {
                foreach (var item in byteRange.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out _)) return false;
                }
                return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (Scan(item, inspection, token)) return true;
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
