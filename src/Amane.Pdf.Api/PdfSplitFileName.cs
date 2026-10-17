using System.Text;

namespace Amane.Pdf.Api;

internal static class PdfSplitFileName
{
    internal static string NormalizeBaseName(string fileName)
    {
        // Both separators apply even when the API runs on Linux.
        var leaf = fileName.AsSpan()[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        if (leaf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in leaf.EnumerateRunes())
        {
            var safe = Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value))
                ? new Rune('_') : rune;
            if (bytes + safe.Utf8SequenceLength > 180) break;
            result.Append(safe.ToString());
            bytes += safe.Utf8SequenceLength;
        }
        var name = result.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return "document";
        // Windows device names remain reserved with extensions, e.g. CON.notes.pdf.
        var device = name.AsSpan();
        var dot = device.IndexOf('.');
        if (dot >= 0) device = device[..dot];
        device = device.TrimEnd(' ');
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (device.Length == 4 && (device[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                device[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)) &&
                "123456789¹²³".Contains(device[3])))
            name = "_" + name; // A device name (including its dotted suffix) may already use all 180 bytes.
        if (Encoding.UTF8.GetByteCount(name) <= 180) return name;
        // Only the ASCII reservation prefix can make the bounded name one byte too long.
        var end = name.Length;
        var last = Rune.GetRuneAt(name, char.IsLowSurrogate(name[end - 1]) ? end - 2 : end - 1);
        return name[..(end - last.Utf16SequenceLength)].TrimEnd(' ', '.');
    }
}
