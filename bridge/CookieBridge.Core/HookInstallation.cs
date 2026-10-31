using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CookieBridge.Core;

/// <summary>
/// Seam for reading/writing the user environment block. Production uses the registry
/// (<c>HKCU\Environment</c>); tests swap in an in-memory store so they never touch a real profile.
/// </summary>
public interface IUserEnvironmentStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

public sealed class RegistryUserEnvironmentStore : IUserEnvironmentStore
{
    public string? Get(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void Set(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
}

public sealed class HookInstallManifest
{
    public string? HookVersion { get; set; }
    public DateTimeOffset InstalledUtc { get; set; }
    public string? HookDllPath { get; set; }
    public string? Sha256 { get; set; }
    public string? InstalledBy { get; set; }
    public List<string> EnvEntriesAdded { get; set; } = [];
}

public sealed record HookInstallState(
    bool Installed,
    bool Enabled,
    bool EnvRegistered,
    bool DllPresent,
    bool DllHashMatches,
    string? HookDllPath,
    string? HookVersion,
    string? EnvValue,
    IReadOnlyList<string> Problems);

/// <summary>
/// Installs/removes the in-process plugin.
///
/// Nothing here touches the VRCVideoCacher installation directory: the plugin is a single managed DLL
/// living under <c>%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\hook</c> and is activated through the
/// CoreCLR startup-hook variable. Steam can update VRCVideoCacher as often as it likes — the plugin
/// still loads, and it degrades to the cookie-file contract if internals change.
/// </summary>
public static class HookInstallation
{
    public const string HookAssemblyName = "CookieBridge.Hook.dll";
    public const string HookEntryType = "StartupHook";

    public static string HookDllPath => Path.Combine(BridgePaths.HookDir, HookAssemblyName);

    /// <summary>Swappable environment store (tests inject an in-memory implementation).</summary>
    public static IUserEnvironmentStore Store { get; set; } = new RegistryUserEnvironmentStore();

    public static HookInstallManifest? ReadManifest()
    {
        try
        {
            var json = AtomicFile.TryReadAllText(BridgePaths.InstallManifest);
            return string.IsNullOrWhiteSpace(json) ? null : BridgeJson.Deserialize<HookInstallManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    public static HookInstallState Inspect()
    {
        var problems = new List<string>();
        var dll = HookDllPath;
        var dllPresent = File.Exists(dll);

        if (!dllPresent) problems.Add("插件 DLL 未安装");

        var enabled = !File.Exists(BridgePaths.DisabledFlag);
        if (!enabled) problems.Add("插件当前被禁用");

        var envValue = ReadUserEnv();
        var envRegistered = envValue != null && EnvContainsHook(envValue, dll);
        if (!envRegistered) problems.Add("DOTNET_STARTUP_HOOKS 未注册本插件");

        var manifest = ReadManifest();
        var hashMatches = false;
        if (dllPresent)
        {
            try
            {
                var actual = Sha256File(dll);
                hashMatches = manifest?.Sha256 == null ||
                              string.Equals(manifest.Sha256, actual, StringComparison.OrdinalIgnoreCase);
                if (!hashMatches) problems.Add("插件 DLL 与安装记录不一致（可能被替换）");
            }
            catch (Exception ex)
            {
                problems.Add($"插件 DLL 无法读取: {ex.GetType().Name}");
            }
        }

        return new HookInstallState(
            Installed: dllPresent && manifest != null,
            Enabled: enabled,
            EnvRegistered: envRegistered,
            DllPresent: dllPresent,
            DllHashMatches: hashMatches,
            HookDllPath: dllPresent ? dll : null,
            HookVersion: manifest?.HookVersion,
            EnvValue: envValue,
            Problems: problems);
    }

    /// <summary>
    /// Deploys the plugin assembly (and, when <paramref name="registerStartupHook"/> is set, also
    /// registers DOTNET_STARTUP_HOOKS for the current user).
    ///
    /// Registration is opt-in on purpose: the Steam build of VRCVideoCacher is a single-file publish
    /// whose host disables the CoreCLR startup-hook feature switch, so the variable cannot help there —
    /// and a user-wide DOTNET_STARTUP_HOOKS would load the plugin into every startup-hook-capable .NET
    /// process the user starts. Only enable it for a self-built fork or a framework-dependent host.
    /// </summary>
    public static bool Install(string sourceHookDll, out string message, bool registerStartupHook = false)
    {
        try
        {
            if (!File.Exists(sourceHookDll))
            {
                message = $"找不到插件文件: {sourceHookDll}";
                return false;
            }

            BridgePaths.EnsureDirectories();
            Directory.CreateDirectory(BridgePaths.HookDir);

            var destination = HookDllPath;
            File.Copy(sourceHookDll, destination, overwrite: true);

            var manifest = new HookInstallManifest
            {
                HookVersion = ReadAssemblyVersion(sourceHookDll),
                InstalledUtc = DateTimeOffset.UtcNow,
                HookDllPath = destination,
                Sha256 = Sha256File(destination),
                InstalledBy = $"{Environment.UserName}@{Environment.MachineName}",
            };

            if (registerStartupHook)
            {
                if (!RegisterEnv(destination, out var envMessage))
                {
                    message = envMessage;
                    return false;
                }

                manifest.EnvEntriesAdded = [destination];
            }
            AtomicFile.WriteAllText(BridgePaths.InstallManifest, BridgeJson.Serialize(manifest));

            SetEnabled(true);
            ClearStaleStatus();

            BridgeLog.Info("install", $"Hook deployed at {destination} (startup hook registration: {registerStartupHook}).");
            message = registerStartupHook
                ? "插件已部署，并已注册到 DOTNET_STARTUP_HOOKS（用户级，仅对支持启动钩子的宿主生效）。"
                : "插件已部署到本机。Steam 版因单文件发布不加载启动钩子，Cookie 走契约模式（Native Host 写入 youtube_cookies.txt）。";
            return true;
        }
        catch (Exception ex)
        {
            BridgeLog.Error("install", "Hook install failed", ex);
            message = $"安装失败: {ex.Message}";
            return false;
        }
    }

    public static bool Uninstall(out string message)
    {
        try
        {
            RemoveEnv(out var envMessage);

            try { if (File.Exists(BridgePaths.DisabledFlag)) File.Delete(BridgePaths.DisabledFlag); } catch { }
            try { if (File.Exists(BridgePaths.InstallManifest)) File.Delete(BridgePaths.InstallManifest); } catch { }
            try
            {
                if (Directory.Exists(BridgePaths.HookDir))
                    Directory.Delete(BridgePaths.HookDir, recursive: true);
            }
            catch (Exception ex)
            {
                message = $"环境变量已清理，但插件目录删除失败: {ex.Message}（VRCVideoCacher 可能仍在运行，退出后重试）";
                return false;
            }

            ClearStaleStatus();
            BridgeLog.Info("install", "Hook uninstalled.");
            message = string.IsNullOrEmpty(envMessage)
                ? "插件已卸载，环境变量已恢复。"
                : $"插件已卸载。{envMessage}";
            return true;
        }
        catch (Exception ex)
        {
            BridgeLog.Error("uninstall", "Hook uninstall failed", ex);
            message = $"卸载失败: {ex.Message}";
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        BridgePaths.EnsureDirectories();
        if (enabled)
        {
            try { if (File.Exists(BridgePaths.DisabledFlag)) File.Delete(BridgePaths.DisabledFlag); } catch { }
        }
        else
        {
            File.WriteAllText(BridgePaths.DisabledFlag,
                $"disabled at {DateTimeOffset.UtcNow:O} by {Environment.UserName}\n");
        }
    }

    public static bool IsEnabled => !File.Exists(BridgePaths.DisabledFlag);

    private static void ClearStaleStatus()
    {
        try
        {
            if (HookStatus.Read() is not { IsFresh: true }) HookStatus.Clear();
        }
        catch
        {
            // ignore
        }
    }

    // ---- environment variable plumbing ----------------------------------

    public static string? ReadUserEnv() => Store.Get(BridgePaths.StartupHooksVariable);

    private static bool EnvContainsHook(string? envValue, string hookPath)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return false;
        return envValue
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(NormalizePath(entry), NormalizePath(hookPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.TrimEnd('\\'); }
    }

    /// <summary>
    /// Appends our DLL to DOTNET_STARTUP_HOOKS without clobbering hooks owned by other tools
    /// (dotnet-counters, profilers, ...). Duplicates are removed first.
    /// </summary>
    public static bool RegisterEnv(string hookDll, out string message)
    {
        message = string.Empty;
        var existing = ReadUserEnv() ?? string.Empty;

        var entries = existing
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.Equals(NormalizePath(entry), NormalizePath(hookDll), StringComparison.OrdinalIgnoreCase))
            .ToList();

        entries.Add(hookDll);
        var merged = string.Join(';', entries);

        try
        {
            Store.Set(BridgePaths.StartupHooksVariable, merged);
            BroadcastEnvironmentChange();

            var verify = ReadUserEnv();
            if (!EnvContainsHook(verify, hookDll))
            {
                message = "写入环境变量后校验失败（可能被安全软件拦截）。";
                return false;
            }

            BridgeLog.Info("install", "DOTNET_STARTUP_HOOKS updated for the current user.");
            return true;
        }
        catch (Exception ex)
        {
            BridgeLog.Error("install", "Failed to write DOTNET_STARTUP_HOOKS", ex);
            message = $"写入环境变量失败: {ex.Message}";
            return false;
        }
    }

    public static bool RemoveEnv(out string message)
    {
        message = string.Empty;
        try
        {
            var existing = ReadUserEnv();
            if (string.IsNullOrWhiteSpace(existing)) return true;

            var hookDir = NormalizePath(BridgePaths.HookDir);
            var entries = existing
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry =>
                {
                    var normalized = NormalizePath(entry);
                    return !normalized.StartsWith(hookDir, StringComparison.OrdinalIgnoreCase)
                           && !normalized.EndsWith(HookAssemblyName, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (entries.Count == 0)
                Store.Set(BridgePaths.StartupHooksVariable, null);
            else
                Store.Set(BridgePaths.StartupHooksVariable, string.Join(';', entries));

            BroadcastEnvironmentChange();
            return true;
        }
        catch (Exception ex)
        {
            message = $"清理环境变量失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Tells explorer.exe (and anything else listening) that the user environment block changed.
    /// Processes started afterwards — including Steam's children — pick the new value up.
    /// </summary>
    public static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero,
                "Environment", 0x0002 /* SMTO_ABORTIFHUNG */, 5000, out _);
        }
        catch
        {
            // best effort
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? ReadAssemblyVersion(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
