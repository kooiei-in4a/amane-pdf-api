using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

if (!OperatingSystem.IsLinux() || args.Length != 4 ||
    !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var maxPages) || maxPages is < 1 or > 300 ||
    !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var maxCharacters) || maxCharacters is < 1 or > 500_000)
    return 1;

return Extract(args[0], args[1], maxPages, maxCharacters);

[SupportedOSPlatform("linux")]
static int Extract(string input, string output, int maxPages, int maxCharacters)
{
    try
    {
        using var document = PdfDocument.Open(input);
        if (document.NumberOfPages > maxPages) return 3;
        using var stream = new FileStream(output, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteStartArray("pages");
        var characters = 0;
        var unavailable = 0;
        var meaningful = 0;
        foreach (var page in document.GetPages())
        {
            // Compare the content stream's character order. Layout-generated spacing
            // and line breaks do not contribute because whitespace is ignored.
            var text = new StringBuilder();
            foreach (var letter in page.Letters)
            foreach (var rune in letter.Value.EnumerateRunes())
            {
                if (++characters > maxCharacters) return 3;
                if (Rune.IsWhiteSpace(rune)) continue;
                meaningful++;
                if (rune == Rune.ReplacementChar ||
                    Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse or UnicodeCategory.Control or UnicodeCategory.Format)
                    unavailable++;
                else
                    text.Append(rune.ToString());
            }
            json.WriteStringValue(text.ToString());
        }
        json.WriteEndArray();
        json.WriteEndObject();
        return meaningful == 0 || unavailable * 5 > meaningful ? 2 : 0;
    }
    catch (OutOfMemoryException) { return 3; }
    catch (IOException) { return 1; }
    catch (Exception) { return 2; }
}
