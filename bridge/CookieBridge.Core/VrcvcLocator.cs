using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CookieBridge.Core;

public sealed record VrcvcInstall(
    string ExePath,
    string InstallDir,
    string DiscoverySource,
    bool LooksLikeSteamInstall)
{
    public bool Exists => File.Exists(ExePath);
}

public sealed record VrcvcVersionInfo(
    string? Version,
    string Source,
    bool FromRunningProcess,
    string? Notes = null)
{
    public bool Known => !string.IsNullOrWhiteSpace(Version) && Version != "unknown";
}

/// <summary>
/// Finds the Steam edition of VRCVideoCacher and reads its version without touching the app.
///
/// Discovery order: Steam registry → every library in <c>libraryfolders.vdf</c> → user override.
/// Version order: running process's assembly version (if our hook is loaded) → PE file version →
/// newest VRCVC log header → <c>unknown</c>.
/// </summary>
public static partial class VrcvcLocator
{
    public const string ExeName = "VRCVideoCacher.exe";
    public const string AppFolderName = "VRCVideoCacher";

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex(@"VRCVideoCacher version (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex LogVersionRegex();

    public static IReadOnlyList<VrcvcInstall> Discover(string? userOverrideDir = null)
    {
        var found = new List<VrcvcInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? dir, string source, bool steam)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir);
                var exe = Path.Combine(full, ExeName);
                if (!File.Exists(exe)) return;
                if (!seen.Add(full)) return;
                found.Add(new VrcvcInstall(exe, full, source, steam));
            }
            catch
            {
                // ignore malformed paths
            }
        }

        Consider(userOverrideDir, "user-override", false);

        foreach (var library in SteamLibraries())
            Consider(Path.Combine(library, "steamapps", "common", AppFolderName), $"steam-library:{library}", true);

        // A few sane extras: portable copies next to the user profile.
        Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppFolderName),
            "program-files", false);
        Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppFolderName),
            "program-files-x86", false);

        return found;
    }

    public static IReadOnlyList<string> SteamLibraries()
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamPath = ReadSteamPath();
        if (steamPath != null && seen.Add(steamPath)) libraries.Add(steamPath);

        foreach (var library in ReadLibraryFolders())
            if (seen.Add(library))
                libraries.Add(library);

        return libraries;
    }

    public static string? ReadSteamPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && path.Length > 0)
                return path.Replace('/', '\\');
        }
        catch
        {
            // registry unavailable
        }
        return null;
    }

    public static IReadOnlyList<string> ReadLibraryFolders()
    {
        var result = new List<string>();
        var steam = ReadSteamPath();
        if (steam == null) return result;

        var vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;

        try
        {
            foreach (var line in File.ReadLines(vdf))
            {
                var match = LibraryPathRegex().Match(line);
                if (!match.Success) continue;
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) result.Add(path);
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("locate", $"libraryfolders.vdf unreadable: {ex.GetType().Name}");
        }

        return result;
    }

    /// <summary>Does this directory really contain a VRCVideoCacher installation?</summary>
    public static bool Validate(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, ExeName));
        }
        catch
        {
            return false;
        }
    }

    public static VrcvcVersionInfo DetectVersion(string? installDir = null, string? runningAssemblyVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(runningAssemblyVersion))
            return new VrcvcVersionInfo(runningAssemblyVersion, "running-process", true);

        var exe = installDir == null ? null : Path.Combine(installDir, ExeName);
        exe ??= Discover().FirstOrDefault()?.ExePath;

        if (exe != null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                var candidate = new[] { info.ProductVersion, info.FileVersion, info.ProductName }
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && Regex.IsMatch(v!, @"\d{4}\.\d"));
                if (candidate != null)
                {
                    var cleaned = candidate.Split('+')[0].Trim();
                    if (Regex.IsMatch(cleaned, @"^\d{4}\.\d"))
                        return new VrcvcVersionInfo(cleaned, "pe-file-version", false);
                }
            }
            catch
            {
                // fall through to logs
            }
        }

        var fromLog = ReadVersionFromLogs();
        return fromLog != null
            ? new VrcvcVersionInfo(fromLog, "log-header", false)
            : new VrcvcVersionInfo(null, "unknown", false, "无法读取版本号");
    }

    public static string? ReadVersionFromLogs()
    {
        try
        {
            var dir = BridgePaths.VrcvcLogDir;
            if (!Directory.Exists(dir)) return null;

            var newest = new DirectoryInfo(dir).GetFiles("VRCVideoCacher*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;

            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 200; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var match = LogVersionRegex().Match(line);
                if (match.Success) return match.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
