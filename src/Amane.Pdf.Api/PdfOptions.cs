namespace Amane.Pdf.Api;

public sealed class PdfOptions
{
    public long MaxFileBytes { get; set; } = 50 * 1024 * 1024;
    public int MaxMergeFiles { get; set; } = 10;
    public long MaxMergeInputBytes { get; set; } = 50 * 1024 * 1024;
    public int MaxSplitParts { get; set; } = 100;
    public long MaxSplitOutputBytes { get; set; } = 50 * 1024 * 1024;
    public long MaxSplitJobBytes { get; set; } = 124 * 1024 * 1024;
    public long CleanJsonLimitBytes { get; set; } = 32 * 1024 * 1024;
    public long CleanOutputLimitBytes { get; set; } = 54 * 1024 * 1024;
    public long CleanJobLimitBytes { get; set; } = 124 * 1024 * 1024;
    public long OverlayJsonLimitBytes { get; set; } = 32 * 1024 * 1024;
    public long OverlayGeneratedLimitBytes { get; set; } = 8 * 1024 * 1024;
    public long OverlayGeneratedJsonLimitBytes { get; set; } = 40 * 1024 * 1024;
    public long OverlayOutputLimitBytes { get; set; } = 62 * 1024 * 1024;
    public long OverlayJobLimitBytes { get; set; } = 124 * 1024 * 1024;
    public int OverlayMaxPages { get; set; } = 1000;
    public int QpdfTimeoutSeconds { get; set; } = 30;
    public int MaxConcurrentProcesses { get; set; } = 2;
    public const int MultipartOverheadBytes = 64 * 1024;
    public long MaxRequestBytes => checked(MaxFileBytes + MultipartOverheadBytes);
    public long MaxMergeRequestBytes => checked(MaxMergeInputBytes + MultipartOverheadBytes);
    public string QpdfPath { get; set; } = "qpdf";
    public string PdfcpuPath { get; set; } = "pdfcpu";
    public string PdfcpuConfigDir { get; set; } = "";
    public string PdfcpuMemoryLimit { get; set; } = "200MiB";
    public long PdfcpuAddressSpaceLimitBytes { get; set; } = 1024 * 1024 * 1024;
    public string PrlimitPath { get; set; } = "/usr/bin/prlimit";
    public long QpdfAddressSpaceLimitBytes { get; set; } = 544 * 1024 * 1024;
    public string QpdfJpegMemory { get; set; } = "600M";
    public string DjpegPath { get; set; } = "djpeg";
    public string CjpegPath { get; set; } = "cjpeg";
    public long JpegAddressSpaceLimitBytes { get; set; } = 64 * 1024 * 1024;
    public long CompressJobLimitBytes { get; set; } = 124 * 1024 * 1024;
    public long CompressPnmLimitBytes { get; set; } = 24 * 1024 * 1024;
    public long CompressMaxPixels { get; set; } = 100_000_000;
    public int CompressMaxImages { get; set; } = 500;
    public int CompressJsonLimitBytes { get; set; } = 16 * 1024 * 1024;
    public int CompressJsonDepth { get; set; } = 64;
    public int CompressSpoolLimitBytes { get; set; } = 16 * 1024 * 1024;
    public int CompressStdoutLimitBytes { get; set; } = 1024 * 1024;
    public int CompressSoftTimeoutSeconds { get; set; } = 21;
    public int CompressImageTimeoutSeconds { get; set; } = 2;
    public int CompressBatchTimeoutSeconds { get; set; } = 2;
    public string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "amane-pdf-api");
}
