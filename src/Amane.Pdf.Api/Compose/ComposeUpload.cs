using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Amane.Pdf.Api;

internal sealed record ComposeUpload(ComposeLayout Layout, Dictionary<string, string> Images)
{
    internal static long BackgroundLimit(PdfOptions options) => Math.Min(PdfOverlayBuilder.InputLimitBytes, options.MaxFileBytes);
    internal static long RequestLimit(PdfOptions options) =>
        BackgroundLimit(options) + ComposeLayout.TotalImageBytes + ComposeLayout.JsonBytes + PdfOptions.MultipartOverheadBytes;

    internal static async Task<ComposeUpload> ReadAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options,
        OverlayCapacity capacity, CancellationToken token)
    {
        using var body = new SizeLimitedReadStream(request.Body, RequestLimit(options));
        var reader = new MultipartReader(MultipartPdfUpload.ReadBoundary(request), body);
        var buffer = new byte[64 * 1024];
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasFile = false;
        byte[]? layout = null;
        long imageBytes = 0;
        while (await MultipartPdfUpload.ReadNextAsync(reader, token) is { } section)
        {
            var disposition = MultipartPdfUpload.ReadDisposition(section);
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var filename = MultipartPdfUpload.ReadFileName(disposition);
            switch (name)
            {
                case "file" when !hasFile && filename.Length != 0:
                    hasFile = true;
                    await Copy(files.InputPath, BackgroundLimit(options));
                    break;
                case "layout" when layout is null:
                    using (var json = new MemoryStream())
                    {
                        await Read(json, ComposeLayout.JsonBytes);
                        layout = json.ToArray();
                    }
                    break;
                case "image" when ComposeLayout.IsId(filename, "as_"):
                    if (images.ContainsKey(filename)) throw new ComposeProblem(400);
                    if (images.Count >= ComposeLayout.MaxImages) throw ComposeProblem.Limit("assets");
                    var path = Path.Combine(files.DirectoryPath, $"compose-upload-{images.Count}.bin");
                    imageBytes += await Copy(path, Math.Min(ComposeLayout.ImageBytes, ComposeLayout.TotalImageBytes - imageBytes));
                    images.Add(filename, path);
                    break;
                default:
                    throw new ComposeProblem(400);
            }

            async Task<long> Copy(string path, long limit)
            {
                await using var output = TemporaryPdfFiles.CreatePrivateFile(path);
                return await Read(output, limit, path);
            }

            async Task<long> Read(Stream output, long limit, string? path = null)
            {
                long count = 0;
                int read;
                while ((read = await MultipartPdfUpload.ReadBodyAsync(section.Body,
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - count + 1)), token)) != 0)
                {
                    count += read;
                    if (count > limit) throw new ComposeProblem(413);
                    if (path is not null) capacity.Reserve(path, count);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (count == 0) throw new ComposeProblem(400);
                return count;
            }
        }
        while (await MultipartPdfUpload.ReadBodyAsync(body, buffer, token) != 0) { }
        if (layout is null) throw new ComposeProblem(400);
        return new(ComposeLayout.Parse(layout, hasFile, ComposeLayout.FontsAvailable(options)), images);
    }

    internal async Task VerifyImagesAsync(CancellationToken token)
    {
        if (Images.Count != Layout.Images.Length) throw new ComposeProblem(400);
        foreach (var image in Layout.Images)
        {
            if (!Images.TryGetValue(image.Id, out var path)) throw new ComposeProblem(400);
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, token);
            if (!string.Equals(image.Sha256, Convert.ToHexStringLower(hash), StringComparison.Ordinal))
                throw new ComposeProblem(400);
        }
    }

    internal async Task<Dictionary<string, StampImage>> PrepareImagesAsync(TemporaryPdfFiles files, PdfOptions options,
        OverlayCapacity capacity, CancellationToken token)
    {
        var images = new Dictionary<string, StampImage>(StringComparer.Ordinal);
        foreach (var (id, input) in Images)
        {
            var length = new FileInfo(input).Length;
            var png = new byte[8];
            await using (var stream = File.OpenRead(input))
                _ = await stream.ReadAtLeastAsync(png, png.Length, false, token);
            if (png.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                var output = Path.Combine(files.DirectoryPath, $"compose-image-{images.Count}.png");
                capacity.Reserve(output, length);
                try
                {
                    var info = await PngSanitizer.WriteAsync(input, output, length, 4_000_000,
                        options.ImagePngWorkingLimitBytes, token);
                    if (info.Width > 2000 || info.Height > 2000) throw ComposeProblem.Limit("imageSide");
                    await PngDataValidator.ValidateAsync(output, token, ComposeLayout.ImageBytes);
                    capacity.Commit(output, length);
                    capacity.Delete(input);
                    images.Add(id, new(output, info.Width, info.Height));
                }
                catch (PdfImageInputException error) when (error.Reason != PdfImageReason.Capacity)
                { throw PdfStampImageException.Unsupported(); }
                catch (PdfImageInputException) { throw new ComposeProblem(422, "image-too-complex"); }
            }
            else
            {
                if (ExifOrientation.Read(input, token) != 1) throw new ComposeProblem(400, "image-orientation");
                var header = JpegHeader.Read(input, ComposeLayout.ImageBytes, 4_000_000, token)
                    ?? throw PdfStampImageException.Unsupported();
                if (header.Width > 2000 || header.Height > 2000) throw ComposeProblem.Limit("imageSide");
                var output = Path.Combine(files.DirectoryPath, $"compose-image-{images.Count}.jpg");
                // A rename within the job directory never retains a second copy.
                File.Move(input, output);
                capacity.Delete(input);
                capacity.Reserve(output, length);
                images.Add(id, new(output, header.Width, header.Height));
            }
        }
        return images;
    }
}
