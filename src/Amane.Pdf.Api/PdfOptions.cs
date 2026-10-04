namespace Amane.Pdf.Api;

public sealed class PdfOptions
{
    public long MaxFileBytes { get; set; } = 50 * 1024 * 1024;
    public int QpdfTimeoutSeconds { get; set; } = 30;
    public int MaxConcurrentProcesses { get; set; } = 2;
    public const int MultipartOverheadBytes = 64 * 1024;
    public long MaxRequestBytes => checked(MaxFileBytes + MultipartOverheadBytes);
    public string QpdfPath { get; set; } = "qpdf";
    public string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "amane-pdf-api");
}
