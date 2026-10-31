using System.Diagnostics;

namespace CookieBridge.Core;

/// <summary>
/// Crash-safe file replacement.
///
/// Every write goes to a sibling temp file that is flushed to disk, then swapped in with
/// <see cref="File.Replace"/> / <see cref="File.Move(string,string,bool)"/>. A reader either sees the
/// complete old file or the complete new one — never a half-written cookie jar. This matters because
/// VRCVideoCacher reads <c>youtube_cookies.txt</c> with <c>File.ReadAllText</c> at arbitrary moments.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text, bool restrictAcl = false, string? backupPath = null)
        => WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text), restrictAcl, backupPath);

    public static void WriteAllBytes(string path, byte[] bytes, bool restrictAcl = false, string? backupPath = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tmp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (restrictAcl)
            {
                if (!FileAcl.TryRestrictToCurrentUser(tmp, out var aclError))
                    BridgeLog.Warn("atomic", $"ACL hardening skipped: {aclError}");
            }

            Swap(tmp, path, backupPath);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Read a file that another process may be replacing at the same time.</summary>
    public static string? TryReadAllText(string path, int attempts = 4)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(30 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                Thread.Sleep(30 * (i + 1));
            }
        }
        return null;
    }

    private static void Swap(string tmp, string destination, string? backupPath)
    {
        if (!File.Exists(destination))
        {
            File.Move(tmp, destination, overwrite: true);
            return;
        }

        try
        {
            if (backupPath != null)
            {
                var backupDir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                File.Replace(tmp, destination, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Replace(tmp, destination, null, ignoreMetadataErrors: true);
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // File.Replace can fail across volumes or on odd filesystems; Move(overwrite) is still atomic
            // on NTFS, which is what every Steam library lives on.
            BridgeLog.Debug("atomic", $"File.Replace fell back to Move: {ex.GetType().Name}");
            File.Move(tmp, destination, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // leftover temp files are cleaned by the manager UI
        }
    }
}

/// <summary>
/// Cross-process gate around the VRCVC cookie file.
///
/// In-process the hook prefers VRCVideoCacher's own <c>YtdlCookieJar</c> semaphore (reflection). This
/// mutex is the fallback, and it is what the native-messaging host uses when VRCVC is not running.
/// </summary>
public static class CookieWriteLock
{
    public static IDisposable? TryAcquire(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, BridgePaths.CookieWriteMutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                }
                catch (AbandonedMutexException)
                {
                    // A previous holder died while writing. The file itself is still consistent thanks to
                    // atomic replace, so taking over is safe.
                    BridgeLog.Warn("lock", "Took over an abandoned cookie-write mutex.");
                    acquired = true;
                }
                if (acquired) return new Releaser(mutex);
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("lock", $"Mutex unavailable: {ex.GetType().Name}");
                if (mutex != null) mutex.Dispose();
                return NullLock.Instance;
            }
            mutex.Dispose();
            Thread.Sleep(120);
        }
        BridgeLog.Warn("lock", "Timed out waiting for the cookie-write mutex.");
        return null;
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { mutex.ReleaseMutex(); } catch { /* already released */ }
            try { mutex.Dispose(); } catch { /* ignore */ }
        }
    }

    private sealed class NullLock : IDisposable
    {
        public static readonly NullLock Instance = new();
        public void Dispose() { }
    }
}
