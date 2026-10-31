namespace VRCVideoCacher.YTDL;

/// <summary>Mirror of upstream <c>VRCVideoCacher.YTDL.YtdlCookieJar</c> (L2): one yt-dlp at a time.</summary>
public static class YtdlCookieJar
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release();
        }
    }
}
