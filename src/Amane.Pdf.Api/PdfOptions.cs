namespace Amane.Pdf.Api;

public sealed class PdfOptions
{
    public string QpdfPath { get; set; } = "qpdf";
    public string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "amane-pdf-api");
}
