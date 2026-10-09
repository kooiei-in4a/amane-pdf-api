namespace Amane.Pdf.Api;

// Only API-generated text models are accepted; no raw pdfcpu JSON or font/file names.
internal sealed record PdfcpuLayer(IReadOnlyDictionary<int, IReadOnlyList<PdfcpuText>> Pages);

internal sealed record PdfcpuText(string Value, string? Anchor = "bc", double X = 0, double Y = 0,
    double Dx = 0, double Dy = 0, double FontSize = 12, string Color = "#000000", double Rotation = 0);

// PR B translates this proven budget overflow into the overlay-specific 422.
internal sealed class PdfcpuCapacityException : Exception;

internal sealed class PdfcpuStartupException() : InvalidOperationException(
    "pdfcpu・日本語フォントの自己テストに失敗しました。");
