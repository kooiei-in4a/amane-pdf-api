namespace Amane.Pdf.Api;

// PdfRedactEndpoint holds the single render slot from before upload through response and cleanup.
// If the slot is occupied, the endpoint returns 503 before reading the upload.
internal sealed class RenderSlot : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    internal IDisposable? TryAcquire() => semaphore.Wait(0) ? new Lease(semaphore) : null;
    public void Dispose() => semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? held = semaphore;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
