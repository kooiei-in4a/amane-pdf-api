using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Amane.Pdf.Api;

public static class MultipartPdfUpload
{
    public static async Task<string> ReadAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException("Invalid multipart content type.");
        }
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
        {
            throw new BadHttpRequestException("Invalid multipart boundary.");
        }
        using var body = new SizeLimitedReadStream(request.Body, options.MaxRequestBytes);
        var reader = new MultipartReader(boundary, body);
        var hasFile = false;
        string? password = null;
        var buffer = new byte[64 * 1024];
        while (await ReadNextAsync(reader, cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadHttpRequestException("Invalid multipart section.");
            }
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
            if (name == "file" && isFile && !hasFile)
            {
                hasFile = true;
                await using var input = TemporaryPdfFiles.CreatePrivateFile(files.InputPath);
                long copied = 0;
                int read;
                while ((read = await ReadBodyAsync(section.Body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, options.MaxFileBytes - copied + 1)), cancellationToken)) != 0)
                {
                    copied += read;
                    if (copied > options.MaxFileBytes) throw new BadHttpRequestException("PDF size limit exceeded.", 413);
                    await input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            else if (name == "password" && !isFile && password is null)
            {
                // A bounded UTF-8 field; never buffer an arbitrary form field or file in memory.
                var bytes = new byte[128];
                var length = 0;
                int read;
                while ((read = await ReadBodyAsync(section.Body, bytes.AsMemory(length), cancellationToken)) != 0)
                {
                    length += read;
                    if (length > 127) throw new BadHttpRequestException("Invalid password length.");
                }
                try
                {
                    password = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                }
                catch (DecoderFallbackException)
                {
                    throw new BadHttpRequestException("Invalid password encoding.");
                }
                if (string.IsNullOrEmpty(password) || password.Any(char.IsControl))
                {
                    throw new BadHttpRequestException("Invalid password.");
                }
            }
            else
            {
                throw new BadHttpRequestException("Unexpected or duplicate multipart field.");
            }
        }
        if (!hasFile || password is null) throw new BadHttpRequestException("Missing required field.");
        // Count an optional MIME epilogue as part of the request before starting qpdf.
        while (await ReadBodyAsync(body, buffer, cancellationToken) != 0) { }
        return password;
    }

    private static async Task<MultipartSection?> ReadNextAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadNextSectionAsync(cancellationToken);
        }
        catch (IOException exception) when (exception is not BadHttpRequestException)
        {
            throw new BadHttpRequestException("Invalid multipart body.");
        }
    }

    private static async ValueTask<int> ReadBodyAsync(Stream body, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await body.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException exception) when (exception is not BadHttpRequestException)
        {
            throw new BadHttpRequestException("Invalid multipart body.");
        }
    }
}
