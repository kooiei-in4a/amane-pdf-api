using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api;

internal sealed class ComposeProblem(int status, string? reason = null, string? limit = null, string[]? elements = null) : Exception
{
    internal int Status { get; } = status;
    internal Dictionary<string, object?> Extensions { get; } = new()
    {
        ["reason"] = reason, ["limit"] = limit, ["elements"] = elements
    };
    internal static ComposeProblem Limit(string limit) => new(422, "limit-exceeded", limit);
}

internal sealed record ComposePage(int Index, int Width, int Height);
internal sealed record ComposeFont(string Id, string Sha256);
internal sealed record ComposeImage(string Id, string Sha256);
internal sealed record ComposeElement(string Id, int Page, string Kind, int[] Rect, string? Image,
    string? Text, string? Font, int? Size, string? Color, string? Align, string? Valign, int? LineHeight);
internal sealed record ComposeLayout(string Layout, ComposeFont[] Fonts, ComposePage[] Pages, bool Blank,
    ComposeImage[] Images, ComposeElement[] Elements)
{
    internal const int JsonBytes = 256 * 1024;
    internal const int ImageBytes = 2 * 1024 * 1024;
    internal const int TotalImageBytes = 10 * 1024 * 1024;
    internal const int MaxImages = 20;
    internal const string Version = "ds-layout/1";
    internal const string GothicHash = "595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673";
    internal const string MinchoHash = "dbcea04578ac1e9d3484525e870ce491bd04361768f4d2ba4b827d96e20f891d";
    internal static string[] FontsAvailable(PdfOptions options) =>
        File.Exists(Path.Combine(options.PdfcpuConfigDir, "pdfcpu", "fonts", PdfcpuProcessor.MinchoFont + ".gob"))
            ? ["gothic", "mincho"] : ["gothic"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, AllowDuplicateProperties = false, MaxDepth = 8
    };

    private static void RequireKeys(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            names.Any(name => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null))
            throw new ComposeProblem(400);
    }

    internal static ComposeLayout Parse(byte[] bytes, bool hasFile, string[] available)
    {
        ComposeLayout layout;
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 8, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (!ValidTokens(root)) throw new ComposeProblem(400);
            RequireKeys(root, "layout", "fonts", "pages", "blank", "images", "elements");
            foreach (var font in root.GetProperty("fonts").EnumerateArray()) RequireKeys(font, "id", "sha256");
            foreach (var page in root.GetProperty("pages").EnumerateArray()) RequireKeys(page, "index", "width", "height");
            foreach (var image in root.GetProperty("images").EnumerateArray()) RequireKeys(image, "id", "sha256");
            foreach (var element in root.GetProperty("elements").EnumerateArray())
            {
                if (element.GetProperty("kind").ValueEquals("image"))
                    RequireKeys(element, "id", "page", "kind", "rect", "image");
                else
                    RequireKeys(element, "id", "page", "kind", "rect", "text", "font", "size", "color", "align", "valign", "lineHeight");
            }
            layout = root.Deserialize<ComposeLayout>(JsonOptions) ?? throw new ComposeProblem(400);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException)
        { throw new ComposeProblem(400); }

        if (layout.Layout != Version) throw new ComposeProblem(422, "unknown-version");
        if (layout.Blank == hasFile || layout.Pages.Length < 1 || (layout.Blank && layout.Pages.Length != 1))
            throw new ComposeProblem(400);
        if (layout.Pages.Length > 30) throw ComposeProblem.Limit("pages");
        if (layout.Images.Length > MaxImages) throw ComposeProblem.Limit("assets");
        if (layout.Elements.Length > 200) throw ComposeProblem.Limit("elements");
        if (layout.Fonts.Length is < 1 or > 2) throw new ComposeProblem(400);

        var fonts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var font in layout.Fonts)
        {
            if (!fonts.Add(font.Id) || !IsHash(font.Sha256)) throw new ComposeProblem(400);
            if (!available.Contains(font.Id)) throw new ComposeProblem(422, "font-unavailable");
            if (font.Sha256 != (font.Id == "gothic" ? GothicHash : MinchoHash))
                throw new ComposeProblem(422, "font-mismatch");
        }
        var pages = new Dictionary<int, ComposePage>();
        foreach (var page in layout.Pages)
        {
            if (page.Index < 1 || page.Index > layout.Pages.Length || page.Width is < 7200 or > 1440000 ||
                page.Height is < 7200 or > 1440000 || !pages.TryAdd(page.Index, page)) throw new ComposeProblem(400);
        }
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in layout.Images)
            if (!IsId(image.Id, "as_") || !IsHash(image.Sha256) || !imageIds.Add(image.Id)) throw new ComposeProblem(400);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var elementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in layout.Elements)
        {
            var r = element.Rect;
            if (!IsId(element.Id, "el_") || !elementIds.Add(element.Id) || !pages.TryGetValue(element.Page, out var page) ||
                r.Length != 4 || r[0] < 0 || r[1] < 0 || r[2] < 100 || r[3] < 100 ||
                (long)r[0] + r[2] > page.Width || (long)r[1] + r[3] > page.Height)
                throw new ComposeProblem(400);
            if (element.Kind == "image")
            {
                if (element.Image is not { } image || !imageIds.Contains(image)) throw new ComposeProblem(400);
                used.Add(image);
                continue;
            }
            if (element.Kind != "text" || element.Font is null || !fonts.Contains(element.Font) ||
                element.Size is not (>= 600 and <= 14400) || element.Size % 50 != 0 ||
                element.Align is not ("left" or "center" or "right") ||
                element.Valign is not ("top" or "middle" or "bottom") ||
                element.LineHeight is not (120 or 150 or 180) ||
                element.Color is null || !Regex.IsMatch(element.Color, @"\A#[0-9A-F]{6}\z"))
                throw new ComposeProblem(400);
            if (element.Text!.Length > 1000) throw ComposeProblem.Limit("fieldCharacters");
        }
        if (!imageIds.SetEquals(used)) throw new ComposeProblem(400);
        if (layout.Elements.GroupBy(item => item.Page).Any(group => group.Count() > 60)) throw ComposeProblem.Limit("elementsPerPage");
        if (layout.Elements.Count(item => item.Kind == "image") > MaxImages) throw ComposeProblem.Limit("imageElements");
        return layout;
    }

    private static bool ValidTokens(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => property.Name.Length > 0 && ValidTokens(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(ValidTokens),
        JsonValueKind.String => value.GetString() is not null,
        JsonValueKind.Number => value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0,
        _ => true
    };

    internal static bool IsId(string id, string prefix) =>
        id.Length > prefix.Length && id.Length <= prefix.Length + 26 && id.StartsWith(prefix, StringComparison.Ordinal) &&
        !id.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdefghijklmnopqrstuvwxyz");

    private static bool IsHash(string value) => value.Length == 64 &&
        !value.AsSpan().ContainsAnyExcept("0123456789abcdef");
}
