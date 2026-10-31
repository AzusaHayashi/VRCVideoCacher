using System.Text.RegularExpressions;

namespace CookieBridge.Core;

public enum CompatLevel
{
    /// <summary>Exactly the build this release was tested against.</summary>
    Verified,
    /// <summary>Older build, same architecture; contract path verified by reflection probe.</summary>
    Supported,
    /// <summary>Newer than anything tested. Enabled, but the user is told to re-run the probe.</summary>
    UntestedNewer,
    /// <summary>Older than the supported floor.</summary>
    UntestedOlder,
    /// <summary>Version string not understood.</summary>
    Unknown,
}

public sealed record CompatReport(
    CompatLevel Level,
    string? Version,
    string Headline,
    IReadOnlyList<string> Notes,
    bool HookAllowed)
{
    public string LevelLabel => Level switch
    {
        CompatLevel.Verified => "已验证",
        CompatLevel.Supported => "兼容（旧版本）",
        CompatLevel.UntestedNewer => "未验证（新版本）",
        CompatLevel.UntestedOlder => "未验证（过旧）",
        _ => "未知版本",
    };
}

/// <summary>
/// Version gate for the plugin. Deliberately optimistic: the design only depends on the yt-dlp cookie
/// file contract plus a handful of reflection probes, so an unknown version is *degraded*, not broken.
/// Nothing here may ever prevent VRCVideoCacher from starting.
/// </summary>
public static partial class CompatibilityMatrix
{
    /// <summary>Build this release was developed and verified against.</summary>
    public static readonly Version VerifiedVersion = new(2026, 10, 29);

    /// <summary>Oldest build whose cookie plumbing is the same shape (Program/YtdlManager/YtdlCookieJar).</summary>
    public static readonly Version MinimumSupported = new(2025, 1, 1);

    [GeneratedRegex(@"^(\d{4})\.(\d{1,2})(?:\.(\d{1,2}))?")]
    private static partial Regex VersionRegex();

    public static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var match = VersionRegex().Match(version.Trim());
        if (!match.Success) return null;

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, build);
    }

    public static CompatReport Evaluate(string? version)
    {
        var parsed = Parse(version);
        if (parsed == null)
        {
            return new CompatReport(CompatLevel.Unknown, version,
                "无法识别 VRCVideoCacher 版本",
                [
                    "插件仍会以“仅 Cookie 文件契约”模式工作。",
                    "请在管理器中运行一次“兼容性探测”以确认反射层是否可用。",
                ],
                HookAllowed: true);
        }

        var notes = new List<string>();

        if (parsed == VerifiedVersion)
        {
            notes.Add("该版本已完整验证：Cookie 文件写入 + 进程内锁 + UI 通知。");
            return new CompatReport(CompatLevel.Verified, version, "已验证版本", notes, true);
        }

        if (parsed > VerifiedVersion)
        {
            notes.Add("检测到比本插件测试版本更新的 VRCVideoCacher。");
            notes.Add("插件会自动降级为稳定契约（Cookie 文件 + 反射探测），不会导致 VRCVC 崩溃。");
            notes.Add("如出现异常，请在管理器中查看“插件日志”中的 layer 探测结果。");
            return new CompatReport(CompatLevel.UntestedNewer, version, "未验证的新版本（已启用降级兼容）", notes, true);
        }

        if (parsed >= MinimumSupported)
        {
            notes.Add("该版本早于本插件的验证版本，但 Cookie 管理结构一致。");
            notes.Add("如反射层缺失某项，会自动回退到 Cookie 文件写入。");
            return new CompatReport(CompatLevel.Supported, version, "兼容的旧版本", notes, true);
        }

        notes.Add("该版本早于本插件支持的最低版本。");
        notes.Add("插件仍可运行，但只会写入 Cookie 文件，不保证进程内通知。");
        return new CompatReport(CompatLevel.UntestedOlder, version, "过旧版本（仅契约模式）", notes, true);
    }
}
