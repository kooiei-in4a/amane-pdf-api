using System.Text.Json;

namespace Amane.Pdf.Api;

// Container roles route their children, but are never themselves neutral.
[Flags]
internal enum RedactObjectRole
{
    Body = 1, Page = 2, Resources = 4, StateMap = 8, ColorMap = 16, PropertyMap = 32,
    ExtGState = 64, ProcSet = 128, ColorSpace = 256, Icc = 512, Oc = 1024,
    Function = 2048, Functions = 4096
}

internal static class RedactObjectRoles
{
    private static readonly RedactObjectRole[] Neutral = [RedactObjectRole.ExtGState, RedactObjectRole.ProcSet,
        RedactObjectRole.ColorSpace, RedactObjectRole.Icc, RedactObjectRole.Oc, RedactObjectRole.Function];
    private const RedactObjectRole NeutralMask = RedactObjectRole.ExtGState | RedactObjectRole.ProcSet |
        RedactObjectRole.ColorSpace | RedactObjectRole.Icc | RedactObjectRole.Oc | RedactObjectRole.Function;

    // ISO 32000-2 Table 57, including deprecated keys, excluding HT and extensions.
    // https://github.com/pdf-association/arlington-pdf-model/blob/master/tsv/latest/GraphicsStateParameter.tsv
    private static readonly string[] StateKeys = ["/Type", "/LW", "/LC", "/LJ", "/ML", "/D", "/RI", "/OP", "/op", "/OPM",
        "/Font", "/BG", "/BG2", "/UCR", "/UCR2", "/TR", "/TR2", "/FL", "/SM", "/SA", "/BM", "/SMask", "/CA", "/ca",
        "/AIS", "/TK", "/UseBlackPtComp", "/HTO", "/HTP"];
    private static readonly string[] IccKeys = ["/N", "/Alternate", "/Range", "/Metadata", "/Length", "/Filter", "/DecodeParms"];
    private static readonly string[] OcgKeys = ["/Type", "/Name", "/Intent", "/Usage"];
    private static readonly string[] OcmdKeys = ["/Type", "/OCGs", "/P", "/VE"];
    private static readonly string[] FunctionKeys = ["/FunctionType", "/Domain", "/Range", "/Size", "/BitsPerSample", "/Order",
        "/Encode", "/Decode", "/C0", "/C1", "/N", "/Functions", "/Bounds", "/Length", "/Filter", "/DecodeParms"];

    internal static bool IsNeutral(JsonElement wrapper, RedactObjectRole roles)
    {
        if (roles == 0 || (roles & ~NeutralMask) != 0) return false;
        var stream = wrapper.TryGetProperty("stream", out var raw);
        var value = stream ? raw.GetProperty("dict") : wrapper.GetProperty("value");
        foreach (var role in Neutral)
            if ((roles & role) != 0 && !Matches(value, stream, role)) return false;
        return true;
    }

    private static bool Matches(JsonElement value, bool stream, RedactObjectRole role) => role switch
    {
        RedactObjectRole.ExtGState => !stream && Keys(value, StateKeys) &&
            (!value.TryGetProperty("/Type", out var type) || Name(type) == "/ExtGState"),
        RedactObjectRole.ProcSet => !stream && value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => Name(v) is not null),
        RedactObjectRole.ColorSpace => !stream && ColorSpace(value),
        RedactObjectRole.Icc => stream && Keys(value, IccKeys) && value.TryGetProperty("/N", out _),
        RedactObjectRole.Oc => !stream && (Name(value, "/Type") switch
        {
            "/OCG" => Keys(value, OcgKeys), "/OCMD" => Keys(value, OcmdKeys), _ => false
        }),
        RedactObjectRole.Function => Keys(value, FunctionKeys),
        _ => false
    };

    private static bool ColorSpace(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;
        var size = value.GetArrayLength();
        return Name(value[0]) switch
        {
            "/ICCBased" or "/CalGray" or "/CalRGB" or "/Lab" => size == 2,
            "/Indexed" => size == 4 && value[3].ValueKind == JsonValueKind.String &&
                (value[3].GetString()!.StartsWith("u:", StringComparison.Ordinal) || value[3].GetString()!.StartsWith("b:", StringComparison.Ordinal)),
            "/Separation" => size == 4,
            "/DeviceN" => size is 4 or 5 && (size == 4 || value[4].ValueKind == JsonValueKind.Object),
            "/Pattern" => size is 1 or 2,
            _ => false
        };
    }

    internal static RedactObjectRole Child(JsonElement dictionary, RedactObjectRole parent, string key)
    {
        if (parent == RedactObjectRole.StateMap) return RedactObjectRole.ExtGState;
        if (parent == RedactObjectRole.ColorMap) return RedactObjectRole.ColorSpace;
        if (parent == RedactObjectRole.PropertyMap) return RedactObjectRole.Oc;
        if (parent == RedactObjectRole.Resources)
            return key switch
            {
                "/ExtGState" => RedactObjectRole.StateMap, "/ProcSet" => RedactObjectRole.ProcSet,
                "/ColorSpace" => RedactObjectRole.ColorMap, "/Properties" => RedactObjectRole.PropertyMap,
                _ => RedactObjectRole.Body
            };
        if (key == "/OC") return RedactObjectRole.Oc;
        if (parent == RedactObjectRole.ExtGState && key is "/TR" or "/TR2" or "/BG" or "/BG2" or "/UCR" or "/UCR2")
            return RedactObjectRole.Function;
        if (parent == RedactObjectRole.Function && key == "/Functions") return RedactObjectRole.Functions;
        if (parent == RedactObjectRole.Oc && Name(dictionary, "/Type") == "/OCMD" && key == "/OCGs") return RedactObjectRole.Oc;
        if (key == "/Resources" && (parent == RedactObjectRole.Page || Name(dictionary, "/Subtype") is "/Form" or "/Type3"))
            return RedactObjectRole.Resources;
        if (key == "/ColorSpace" && Name(dictionary, "/Subtype") == "/Image") return RedactObjectRole.ColorSpace;
        return RedactObjectRole.Body;
    }

    internal static RedactObjectRole Element(JsonElement array, RedactObjectRole parent, int index)
    {
        if (parent is RedactObjectRole.Functions or RedactObjectRole.Function) return RedactObjectRole.Function;
        if (parent == RedactObjectRole.Oc) return RedactObjectRole.Oc;
        if (parent != RedactObjectRole.ColorSpace || array.GetArrayLength() == 0) return RedactObjectRole.Body;
        return (Name(array[0]), index) switch
        {
            ("/ICCBased", 1) => RedactObjectRole.Icc,
            ("/Indexed" or "/Pattern", 1) => RedactObjectRole.ColorSpace,
            ("/Separation" or "/DeviceN", 2) => RedactObjectRole.ColorSpace,
            ("/Separation" or "/DeviceN", 3) => RedactObjectRole.Function,
            _ => RedactObjectRole.Body
        };
    }

    private static bool Keys(JsonElement value, string[] allowed) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));
    private static string? Name(JsonElement dictionary, string key) => dictionary.ValueKind == JsonValueKind.Object &&
        dictionary.TryGetProperty(key, out var value) ? Name(value) : null;
    private static string? Name(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        value.GetString()!.StartsWith("/", StringComparison.Ordinal) ? value.GetString() : null;
}
