using System.Diagnostics;

namespace CookieBridge.Core;

/// <summary>
/// Detects an in-flight yt-dlp run.
///
/// Upstream's <c>YtdlCookieJar</c> comment explains why this matters: yt-dlp rewrites the cookie jar
/// on exit because YouTube rotates session tokens. Writing the jar from outside while yt-dlp holds it
/// silently discards one side of that rotation, and the next request comes back as
/// "Sign in to confirm you're not a bot". An out-of-process writer cannot take VRCVideoCacher's
/// in-process semaphore, so it waits for the short-lived yt-dlp process to finish instead.
/// </summary>
public static class YtdlpActivity
{
    private static readonly string[] ProcessNames = ["yt-dlp", "yt-dlp-stub"];

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) continue;

                foreach (var process in processes)
                    process.Dispose();

                return true;
            }
            catch
            {
                // process enumeration can fail; assume idle
            }
        }
        return false;
    }

    /// <summary>Block until no yt-dlp is running, or the deadline passes. Returns true when idle.</summary>
    public static bool WaitUntilIdle(TimeSpan maxWait, TimeSpan pollInterval)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < maxWait)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(pollInterval);
        }
        return !IsRunning();
    }
}
