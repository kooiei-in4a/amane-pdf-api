namespace Amane.Pdf.Api;

// Held for the whole request by future redact / PDF-to-image / OCR callers.
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
