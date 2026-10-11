using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Amane.Pdf.Api;

public static class MultipartPdfUpload
{
    public static async Task<string> ReadAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options, CancellationToken cancellationToken)
        => await ReadCoreAsync(request, files, options, requirePassword: true, cancellationToken)
            ?? throw new BadHttpRequestException("Missing required field.");

    public static async Task ReadFileAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options, CancellationToken cancellationToken)
        => _ = await ReadCoreAsync(request, files, options, requirePassword: false, cancellationToken);

    public static Task<IReadOnlyList<string>> ReadMergeFilesAsync(HttpRequest request, TemporaryPdfFiles files,
        PdfOptions options, CancellationToken cancellationToken)
        => ReadMultipleAsync(request, options, 2, options.MaxMergeFiles, options.MaxMergeInputBytes,
            options.MaxMergeRequestBytes, files.MergeInputPath, cancellationToken);

    internal static Task<IReadOnlyList<string>> ReadImageFilesAsync(HttpRequest request, TemporaryPdfFiles files,
        PdfOptions options, CancellationToken token)
        => ReadMultipleAsync(request, options, 1, options.MaxImageFiles, options.MaxImageInputBytes,
            options.MaxImageRequestBytes, index => files.ImagePath(index, "upload.bin"), token);

    private static async Task<IReadOnlyList<string>> ReadMultipleAsync(HttpRequest request, PdfOptions options,
        int minimum, int maximum, long totalLimit, long requestLimit, Func<int, string> makePath,
        CancellationToken cancellationToken)
    {
        var boundary = ReadBoundary(request);
        using var body = new SizeLimitedReadStream(request.Body, requestLimit);
        var reader = new MultipartReader(boundary, body);
        var paths = new List<string>();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (await ReadNextAsync(reader, cancellationToken) is { } section)
        {
            var disposition = ReadDisposition(section);
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
            if (name != "file" || !isFile)
                throw new BadHttpRequestException("Unexpected multipart field.");
            // Reject the N+1 part before creating or writing another temporary file.
            if (paths.Count >= maximum)
                throw new BadHttpRequestException("PDF file count limit exceeded.");

            var path = makePath(paths.Count + 1);
            var remaining = Math.Min(options.MaxFileBytes, totalLimit - total);
            total += await CopyFileAsync(section.Body, path, remaining, buffer, cancellationToken);
            paths.Add(path);
        }
        if (paths.Count < minimum) throw new BadHttpRequestException("Required file count not met.");
        // Count an optional MIME epilogue before starting any qpdf process.
        while (await ReadBodyAsync(body, buffer, cancellationToken) != 0) { }
        return paths;
    }

    private static async Task<string?> ReadCoreAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options,
        bool requirePassword, CancellationToken cancellationToken)
    {
        var boundary = ReadBoundary(request);
        using var body = new SizeLimitedReadStream(request.Body, options.MaxRequestBytes);
        var reader = new MultipartReader(boundary, body);
        var hasFile = false;
        string? password = null;
        var buffer = new byte[64 * 1024];
        while (await ReadNextAsync(reader, cancellationToken) is { } section)
        {
            var disposition = ReadDisposition(section);
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
            if (name == "file" && isFile && !hasFile)
            {
                hasFile = true;
                await CopyFileAsync(section.Body, files.InputPath, options.MaxFileBytes, buffer, cancellationToken);
            }
            else if (requirePassword && name == "password" && !isFile && password is null)
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
        if (!hasFile || (requirePassword && password is null)) throw new BadHttpRequestException("Missing required field.");
        // Count an optional MIME epilogue as part of the request before starting qpdf.
        while (await ReadBodyAsync(body, buffer, cancellationToken) != 0) { }
        return password;
    }

    private static string ReadBoundary(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Invalid multipart content type.");
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
            throw new BadHttpRequestException("Invalid multipart boundary.");
        return boundary;
    }

    private static ContentDispositionHeaderValue ReadDisposition(MultipartSection section)
    {
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
            !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Invalid multipart section.");
        return disposition;
    }

    private static async Task<long> CopyFileAsync(Stream body, string path, long limit, byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using var input = TemporaryPdfFiles.CreatePrivateFile(path);
        long copied = 0;
        int read;
        while ((read = await ReadBodyAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - copied + 1)), cancellationToken)) != 0)
        {
            copied += read;
            if (copied > limit) throw new BadHttpRequestException("PDF size limit exceeded.", 413);
            await input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return copied;
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
