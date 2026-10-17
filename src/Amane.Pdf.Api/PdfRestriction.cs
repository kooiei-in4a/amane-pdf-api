namespace Amane.Pdf.Api;

internal sealed record PdfRestriction(string OwnerPassword, string UserPassword, string Print, string Copy, string Edit)
{
    public override string ToString() => $"PdfRestriction {{ Print = {Print}, Copy = {Copy}, Edit = {Edit} }}";
}
