using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CookieBridge.Core;

/// <summary>
/// Every filesystem location the bridge touches. Values follow docs/CONTRACT.md §2.
/// Nothing here depends on a specific VRCVideoCacher version: the cookie file path is the
/// yt-dlp <c>--cookies</c> contract used by upstream (<c>YTDL/YtdlManager.cs</c>).
/// </summary>
public static class BridgePaths
{
    public const string ProductName = "VRCVideoCacher-CookieBridge";
    public const string NativeHostName = "com.vrcvideocacher.cookiebridge";
    public const string PipeName = "VRCVideoCacherCookieBridge";

    /// <summary>Environment variable consumed by the CoreCLR host to load the in-process plugin.</summary>
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    /// <summary>Optional override for the hook directory (normally derived from the assembly location).</summary>
    public const string HookDirVariable = "VRCCB_HOOK_DIR";

    public const string VaultEntropy = "VRCVideoCacher-CookieBridge/vault/v1";

    /// <summary>
    /// Test/portable override: <c>VRCCB_ROOT</c> relocates every bridge artefact. Used by the automated
    /// test suite so it can never touch a real user vault.
    /// </summary>
    public const string RootOverrideVariable = "VRCCB_ROOT";

    /// <summary>Test override for VRCVideoCacher's data directory (<c>VRCCB_VRCVC_DATA</c>).</summary>
    public const string VrcvcDataOverrideVariable = "VRCCB_VRCVC_DATA";

    // Deliberately not cached: VRCCB_ROOT is documented as a per-process override and the automated
    // test suite flips it between cases. Caching it made the isolation of one test leak into the next.
    public static string Root => ResolveRoot();

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { return Path.GetFullPath(custom); } catch { /* fall through */ }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
    }

    public static string VaultFile => Path.Combine(Root, "vault.json");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(LogDir, "bridge.log");
    public static string StateDir => Path.Combine(Root, "state");
    public static string PendingSyncMarker => Path.Combine(StateDir, "pending.sync");
    public static string HookStatusFile => Path.Combine(StateDir, "hook-status.json");
    public static string DisabledFlag => Path.Combine(Root, "disabled.flag");
    public static string InstallManifest => Path.Combine(Root, "install.json");
    public static string HookDir => Path.Combine(Root, "hook");

    /// <summary><c>%APPDATA%\VRCVideoCacher</c> — mirrors upstream <c>Program.DataPath</c>.</summary>
    public static string VrcvcDataDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(VrcvcDataOverrideVariable);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try { return Path.GetFullPath(custom); } catch { /* fall through */ }
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCVideoCacher");
        }
    }

    /// <summary>The single interface shared with VRCVideoCacher: its yt-dlp cookie jar.</summary>
    public static string VrcvcCookieFile => Path.Combine(VrcvcDataDir, "youtube_cookies.txt");

    public static string VrcvcConfigFile => Path.Combine(VrcvcDataDir, "Config.json");
    public static string VrcvcLogDir => Path.Combine(VrcvcDataDir, "Logs");

    /// <summary>Named mutex shared by every process that may write the VRCVC cookie file.</summary>
    public static string CookieWriteMutexName => @"Local\VRCVideoCacherCookieBridge.CookieWrite";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(StateDir);
    }

    /// <summary>Debug helper: the file is intentionally all-lowercase so it survives case-insensitive paths.</summary>
    public static string DescribeForLog(string path) => path;
}

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value, bool compact = false) =>
        JsonSerializer.Serialize(value, compact ? Compact : Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
