namespace Amane.Pdf.Api;

internal enum PdfRedactReason { TooComplex, UnsupportedPdf, OutputTooLarge, TooManyPages }

internal sealed class PdfRedactException(PdfRedactReason reason) : Exception
{
    internal PdfRedactReason Reason { get; } = reason;
    internal string Code => Reason switch
    {
        PdfRedactReason.TooComplex => "too-complex",
        PdfRedactReason.UnsupportedPdf => "unsupported-pdf",
        PdfRedactReason.OutputTooLarge => "output-too-large",
        PdfRedactReason.TooManyPages => "too-many-pages",
        _ => throw new InvalidOperationException()
    };
    internal string Title => Reason switch
    {
        PdfRedactReason.TooComplex => "このPDFは黒塗りの処理上限を超えています。",
        PdfRedactReason.UnsupportedPdf => "このPDFは、黒塗りで安全に処理できない構造を含んでいます。",
        PdfRedactReason.OutputTooLarge => "黒塗りしたページの画像が大きくなりすぎるため、処理できませんでした。黒塗りするページを減らしてください。",
        PdfRedactReason.TooManyPages => "黒塗りするページとフォントを共有するページも画像にする必要があり、上限の30ページを超えました。PDFを分割してから黒塗りしてください。",
        _ => throw new InvalidOperationException()
    };
    internal static PdfRedactException Complex() => new(PdfRedactReason.TooComplex);
    internal static PdfRedactException Unsupported() => new(PdfRedactReason.UnsupportedPdf);
    internal static PdfRedactException Output() => new(PdfRedactReason.OutputTooLarge);
}
