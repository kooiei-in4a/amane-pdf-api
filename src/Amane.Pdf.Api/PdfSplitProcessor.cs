using System.IO.Compression;

namespace Amane.Pdf.Api;

internal sealed class PdfSplitProcessor(QpdfProcessor qpdf, PdfOptions options, TemporaryPdfFiles files)
{
    internal async Task SplitAsync(PdfSplitPlan plan, CancellationToken token)
    {
        await qpdf.ValidateAsync(files, token);
        var parts = plan.CreateParts(await qpdf.GetPageCountAsync(files, token));
        var accepted = await WriteZipAsync(TemporaryPdfFiles.CreatePrivateFile(files.OutputPath), parts, token);
        token.ThrowIfCancellationRequested();
        // Inspect the completed file after the owning stream has closed.
        var capacity = new PdfSplitCapacity(new FileInfo(files.InputPath).Length,
            options.MaxSplitOutputBytes, options.MaxSplitJobBytes);
        capacity.AcceptPart(accepted);
        capacity.ReleasePart();
        capacity.CheckZipLength(new FileInfo(files.OutputPath).Length);
    }

    // Takes ownership of output. Tests can supply a faulting seekable stream without a production hook.
    internal async Task<long> WriteZipAsync(Stream output, IReadOnlyList<SplitPart> parts, CancellationToken token,
        long zipOverheadBytes = PdfSplitCapacity.SplitZipOverheadBytes)
    {
        Stream? ownedOutput = output;
        Stream? partInput = null;
        PdfSplitZipStream? bounded = null;
        try
        {
            var capacity = new PdfSplitCapacity(new FileInfo(files.InputPath).Length,
                options.MaxSplitOutputBytes, options.MaxSplitJobBytes, zipOverheadBytes);
            bounded = new(output, capacity, token);
            var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var part in parts)
            {
                token.ThrowIfCancellationRequested();
                var budget = capacity.PartBudget(output.Length);
                var path = files.SplitPartPath(part.Index);
                using (TemporaryPdfFiles.CreatePrivateFile(path)) { }
                await qpdf.SplitPartAsync(files.InputPath, part.PageRange, path, budget, token);
                capacity.AcceptPart(new FileInfo(path).Length);
                var entry = archive.CreateEntry(part.EntryName, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                partInput = File.OpenRead(path);
                // No using: failed copies must not trigger entry/archive finalization during unwinding.
                var entryStream = entry.Open();
                await partInput.CopyToAsync(entryStream, 128 * 1024, token);
                entryStream.Dispose();
                Close(ref partInput);
                File.Delete(path);
                capacity.ReleasePart();
            }
            archive.Dispose();
            token.ThrowIfCancellationRequested();
            capacity.CheckZipLength(output.Length);
            Close(ref ownedOutput);
            token.ThrowIfCancellationRequested();
            return capacity.AcceptedPdfBytes;
        }
        catch
        {
            bounded?.Abort();
            throw;
        }
        finally
        {
            // Only streams left over after a primary failure remain here. Preserve that failure.
            try { Close(ref partInput); } catch { }
            try { Close(ref ownedOutput); } catch { }
        }
    }

    private static void Close(ref Stream? stream)
    {
        var owned = stream;
        stream = null; // A throwing close is never retried.
        owned?.Dispose();
    }
}
