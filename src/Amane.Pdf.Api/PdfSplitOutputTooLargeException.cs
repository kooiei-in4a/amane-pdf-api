namespace Amane.Pdf.Api;

internal sealed class PdfSplitOutputTooLargeException() : Exception(Title)
{
    internal const string Title = "分割処理の容量上限に達したため、処理できませんでした。含めるページを減らすか、分け方を変更してお試しください。";
}
