using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Amane.Pdf.TextExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || (args.Length != 4 && (args.Length != 6 || args[0] != "--diff")) ||
            !int.TryParse(args[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var maxPages) || maxPages is < 1 or > 300 ||
            !int.TryParse(args[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var maxCharacters) || maxCharacters is < 1 or > 500_000)
            return 1;

        try
        {
            return args.Length == 4
                ? Extract(args[0], args[1], maxPages, maxCharacters)
                : Diff(args[1], args[2], args[3], maxPages, maxCharacters);
        }
        catch (Exception exception) when (exception is OutOfMemoryException or CompareLimitException) { return 3; }
        // Unknown failures are internal errors. Never log exceptions or document contents.
        catch (Exception) { return 1; }
    }

    [SupportedOSPlatform("linux")]
    private static int Extract(string input, string output, int maxPages, int maxCharacters)
    {
        try
        {
            using var document = PdfDocument.Open(input);
            if (document.NumberOfPages > maxPages) return 3;
            using var stream = CreatePrivateFile(output);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteStartArray("pages");
            var scanned = 0;
            var scanLimit = Math.Max(4096, 4 * maxCharacters);
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
                    if (++scanned > scanLimit) return 3;
                    if (Rune.IsWhiteSpace(rune)) continue;
                    meaningful++;
                    if (rune == Rune.ReplacementChar ||
                        Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse or UnicodeCategory.Control or UnicodeCategory.Format)
                        unavailable++;
                    else
                    {
                        if (++characters > maxCharacters) return 3;
                        text.Append(rune.ToString());
                    }
                }
                json.WriteStringValue(text.ToString());
            }
            json.WriteEndArray();
            json.WriteEndObject();
            return meaningful == 0 || unavailable * 5 > meaningful ? 2 : 0;
        }
        catch (Exception exception) when (exception is PdfDocumentFormatException or PdfDocumentStackDepthException) { return 4; }
    }

    [SupportedOSPlatform("linux")]
    private static int Diff(string oldPath, string newPath, string output, int maxPages, int maxCharacters)
    {
        var oldText = ReadText(oldPath, maxPages, maxCharacters);
        var newText = ReadText(newPath, maxPages, maxCharacters);
        if (oldText.Text.Length == 0 || newText.Text.Length == 0) return 2;

        var result = TextDiff.Compare(oldText, newText);
        using var stream = CreatePrivateFile(output);
        JsonSerializer.Serialize(stream, result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return 0;
    }

    private static CompareText ReadText(string path, int maxPages, int maxCharacters)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        var pages = json.RootElement.GetProperty("pages");
        if (pages.GetArrayLength() > maxPages) throw new CompareLimitException();
        var text = new List<Rune>();
        var pageNumbers = new List<int>();
        var pageNumber = 0;
        foreach (var page in pages.EnumerateArray())
        {
            pageNumber++;
            foreach (var rune in (page.GetString() ?? "").EnumerateRunes())
            {
                if (text.Count >= maxCharacters) throw new CompareLimitException();
                text.Add(rune);
                pageNumbers.Add(pageNumber);
            }
        }
        return new([.. text], [.. pageNumbers], pageNumber);
    }

    [SupportedOSPlatform("linux")]
    private static FileStream CreatePrivateFile(string output) => new(output, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    });
}
