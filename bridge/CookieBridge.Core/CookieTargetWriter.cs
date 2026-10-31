using System.Diagnostics;
using System.Text;

namespace CookieBridge.Core;

public enum TargetWriteOutcome
{
    Written,
    Unchanged,
    Invalid,
    Locked,
    Failed,
}

public sealed record TargetFileState(
    bool Exists,
    bool Readable,
    bool Valid,
    int CookieCount,
    bool LoginCookiePresent,
    DateTimeOffset? LastWriteUtc,
    long SizeBytes,
    string? Error)
{
    public static TargetFileState Missing { get; } =
        new(false, false, false, 0, false, null, 0, null);
}

/// <summary>
/// Writes the cookie jar VRCVideoCacher actually consumes:
/// <c>%APPDATA%\VRCVideoCacher\youtube_cookies.txt</c>.
///
/// This is the *only* coupling point with VRCVideoCacher and it is a yt-dlp contract, not an internal
/// API — it survives application updates. Writers always take <see cref="CookieWriteLock"/> (or, from
/// inside the process, VRCVC's own <c>YtdlCookieJar</c>) so yt-dlp's token rotation is never lost.
/// </summary>
public static class CookieTargetWriter
{
    public const string TargetOverrideVariable = "VRCCB_TARGET_FILE";

    public static string TargetPath
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(TargetOverrideVariable);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try { return Path.GetFullPath(custom); } catch { /* fall through */ }
            }
            return BridgePaths.VrcvcCookieFile;
        }
    }

    public static TargetFileState ReadState()
    {
        var path = TargetPath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return TargetFileState.Missing;

            var text = AtomicFile.TryReadAllText(path);
            if (text == null)
                return new TargetFileState(true, false, false, 0, false,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length, "unreadable");

            var summary = NetscapeCookieFile.Summarize(text);
            return new TargetFileState(
                true,
                true,
                NetscapeCookieFile.IsValidForVrcvc(text),
                summary.CookieCount,
                summary.LoginCookiePresent,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length,
                null);
        }
        catch (Exception ex)
        {
            return new TargetFileState(false, false, false, 0, false, null, 0, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Atomically install <paramref name="netscapeText"/> as the live cookie jar.
    /// Callers that already hold VRCVC's in-process jar lock must pass <paramref name="alreadyLocked"/>.
    /// </summary>
    public static TargetWriteOutcome Write(
        string netscapeText,
        out string detail,
        bool alreadyLocked = false,
        TimeSpan? lockTimeout = null,
        string? backupPath = null,
        TimeSpan? waitForYtdlp = null)
    {
        detail = string.Empty;
        try
        {
            var canonical = NetscapeCookieFile.Canonicalize(netscapeText);
            if (!NetscapeCookieFile.IsUsable(canonical))
            {
                detail = "cookies-not-usable";
                return TargetWriteOutcome.Invalid;
            }

            // Out-of-process writers cannot take VRCVideoCacher's in-process YtdlCookieJar gate, so they
            // wait for the short-lived yt-dlp run to end instead of clobbering its token rotation.
            if (!alreadyLocked && waitForYtdlp is { } budget && budget > TimeSpan.Zero)
            {
                if (!YtdlpActivity.WaitUntilIdle(budget, TimeSpan.FromMilliseconds(300)))
                {
                    detail = "ytdlp-still-running";
                    BridgeLog.Warn("target", "yt-dlp still running; deferring the cookie write.");
                    return TargetWriteOutcome.Locked;
                }
            }

            IDisposable? handle = null;
            if (!alreadyLocked)
            {
                handle = CookieWriteLock.TryAcquire(lockTimeout ?? TimeSpan.FromSeconds(5));
                if (handle == null)
                {
                    detail = "lock-timeout";
                    return TargetWriteOutcome.Locked;
                }
            }

            try
            {
                var current = AtomicFile.TryReadAllText(TargetPath);
                if (current != null &&
                    string.Equals(NetscapeCookieFile.Canonicalize(current), canonical, StringComparison.Ordinal))
                {
                    detail = "identical";
                    return TargetWriteOutcome.Unchanged;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
                AtomicFile.WriteAllText(TargetPath, canonical, restrictAcl: false, backupPath: backupPath);

                var summary = NetscapeCookieFile.Summarize(canonical);
                detail = $"{summary.CookieCount} cookies, login={summary.LoginCookiePresent}";
                BridgeLog.Info("target", $"Wrote VRCVC cookie file ({detail}).");
                return TargetWriteOutcome.Written;
            }
            finally
            {
                handle?.Dispose();
            }
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            BridgeLog.Error("target", "Failed writing the VRCVC cookie file", ex);
            return TargetWriteOutcome.Failed;
        }
    }

    /// <summary>Backup kept next to the original by <see cref="Write"/>.</summary>
    public static string DefaultBackupPath => TargetPath + ".cookiebridge.bak";

    public static string Describe(TargetWriteOutcome outcome) => outcome switch
    {
        TargetWriteOutcome.Written => "已写入",
        TargetWriteOutcome.Unchanged => "内容相同，跳过",
        TargetWriteOutcome.Invalid => "Cookie 无效",
        TargetWriteOutcome.Locked => "等待写入锁超时",
        TargetWriteOutcome.Failed => "写入失败",
        _ => outcome.ToString(),
    };
}

/// <summary>Detect whether VRCVideoCacher currently runs, and whether our hook is inside it.</summary>
public static class VrcvcProcessState
{
    public static Process? FindRunning()
    {
        try
        {
            return Process.GetProcessesByName("VRCVideoCacher").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRunning() => FindRunning() != null;

    public static int? RunningPid()
    {
        var p = FindRunning();
        try { return p?.Id; } finally { p?.Dispose(); }
    }
}
