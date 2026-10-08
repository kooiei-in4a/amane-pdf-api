namespace Amane.Pdf.Api;

internal enum PdfUnlockReason
{
    NotEncrypted,
    NoOpenPassword,
    WrongPassword,
    InvalidPdf
}

internal sealed class PdfUnlockException(PdfUnlockReason reason) : Exception("PDF unlock input is unsupported or invalid.")
{
    public PdfUnlockReason Reason { get; } = reason;
}
