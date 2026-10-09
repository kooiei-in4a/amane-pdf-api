using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Amane.Pdf.Api;

// Each element starts at a unique position in the document's retained UTF-8 buffer.
// Use that buffer itself, so parsing a copied input or leading whitespace is safe.
// The comparer and its sets must not outlive the JsonDocument.
internal sealed class PdfJsonElementIdentityComparer(JsonDocument document) : IEqualityComparer<JsonElement>
{
    public bool Equals(JsonElement left, JsonElement right)
        => Offset(left) == Offset(right);

    public int GetHashCode(JsonElement element) => Offset(element);

    private int Offset(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return -1;
        var source = JsonMarshal.GetRawUtf8Value(document.RootElement);
        var raw = JsonMarshal.GetRawUtf8Value(element);
        var offset = Unsafe.ByteOffset(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(raw));
        // Managed byrefs remain valid if GC moves the buffer. Never dereference an
        // offset, and reject elements outside the source rather than hashing them.
        if (offset < 0 || offset >= source.Length || raw.Length > source.Length - offset)
            throw new InvalidOperationException("PDF metadata identity is invalid.");
        return (int)offset;
    }
}
