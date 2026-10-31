using Microsoft.Win32;

namespace CookieBridge.Core;

public enum NativeHostFlavor
{
    Chromium,
    Firefox,
}

public sealed record BrowserHostTarget(string RegistrySubKey, string DisplayName, NativeHostFlavor Flavor);

/// <summary>
/// Registers the native-messaging host for every Chromium-family browser and Firefox, per user
/// (HKCU — no administrator rights needed, nothing machine-wide is modified).
/// </summary>
public static class NativeHostRegistration
{
    public static string HostDir => Path.Combine(BridgePaths.Root, "host");
    public static string ChromiumManifestPath => Path.Combine(HostDir, "native-host-chromium.json");
    public static string FirefoxManifestPath => Path.Combine(HostDir, "native-host-firefox.json");

    public static IReadOnlyList<BrowserHostTarget> Targets { get; } =
    [
        new(@"Software\Google\Chrome\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Google Chrome", NativeHostFlavor.Chromium),
        new(@"Software\Chromium\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Chromium", NativeHostFlavor.Chromium),
        new(@"Software\Microsoft\Edge\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Microsoft Edge", NativeHostFlavor.Chromium),
        new(@"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Brave", NativeHostFlavor.Chromium),
        new(@"Software\Vivaldi\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Vivaldi", NativeHostFlavor.Chromium),
        new(@"Software\Mozilla\NativeMessagingHosts\" + BridgePaths.NativeHostName, "Firefox", NativeHostFlavor.Firefox),
    ];

    public static bool Register(string hostExePath, IEnumerable<string> chromiumExtensionIds,
        IEnumerable<string> firefoxExtensionIds, out string message)
    {
        try
        {
            if (!File.Exists(hostExePath))
            {
                message = $"找不到本地写入程序: {hostExePath}";
                return false;
            }

            Directory.CreateDirectory(HostDir);

            var chromiumIds = chromiumExtensionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var firefoxIds = firefoxExtensionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var chromiumManifest = new Dictionary<string, object?>
            {
                ["name"] = BridgePaths.NativeHostName,
                ["description"] = "VRCVideoCacher CookieBridge local cookie writer",
                ["path"] = hostExePath,
                ["type"] = "stdio",
                ["allowed_origins"] = chromiumIds.Select(id =>
                    id.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                        ? id
                        : $"chrome-extension://{id}/").ToList(),
            };
            AtomicFile.WriteAllText(ChromiumManifestPath, BridgeJson.Serialize(chromiumManifest));

            var firefoxManifest = new Dictionary<string, object?>
            {
                ["name"] = BridgePaths.NativeHostName,
                ["description"] = "VRCVideoCacher CookieBridge local cookie writer",
                ["path"] = hostExePath,
                ["type"] = "stdio",
                ["allowed_extensions"] = firefoxIds,
            };
            AtomicFile.WriteAllText(FirefoxManifestPath, BridgeJson.Serialize(firefoxManifest));

            var registered = new List<string>();
            foreach (var target in Targets)
            {
                var manifest = target.Flavor == NativeHostFlavor.Firefox ? FirefoxManifestPath : ChromiumManifestPath;
                if (target.Flavor == NativeHostFlavor.Firefox && firefoxIds.Count == 0) continue;
                if (target.Flavor == NativeHostFlavor.Chromium && chromiumIds.Count == 0) continue;

                using var key = Registry.CurrentUser.CreateSubKey(target.RegistrySubKey, writable: true);
                if (key == null) continue;
                key.SetValue(string.Empty, manifest, RegistryValueKind.String);
                registered.Add(target.DisplayName);
            }

            message = registered.Count > 0
                ? $"Native Messaging Host 已注册: {string.Join(", ", registered)}"
                : "没有注册任何浏览器（缺少扩展 ID？）";
            BridgeLog.Info("install", message);
            return registered.Count > 0;
        }
        catch (Exception ex)
        {
            BridgeLog.Error("install", "Native host registration failed", ex);
            message = $"注册失败: {ex.Message}";
            return false;
        }
    }

    public static bool Unregister(out string message)
    {
        try
        {
            var removed = new List<string>();
            foreach (var target in Targets)
            {
                using var key = Registry.CurrentUser.OpenSubKey(target.RegistrySubKey, writable: true);
                if (key == null) continue;
                key.Close();
                Registry.CurrentUser.DeleteSubKeyTree(target.RegistrySubKey, throwOnMissingSubKey: false);
                removed.Add(target.DisplayName);
            }

            message = removed.Count > 0
                ? $"已移除 Native Messaging 注册: {string.Join(", ", removed)}"
                : "没有找到已注册的浏览器";
            return true;
        }
        catch (Exception ex)
        {
            message = $"移除注册失败: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<BrowserHostTarget> GetRegisteredTargets()
    {
        var result = new List<BrowserHostTarget>();
        foreach (var target in Targets)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(target.RegistrySubKey);
                if (key?.GetValue(string.Empty) != null) result.Add(target);
            }
            catch
            {
                // ignore
            }
        }
        return result;
    }

    public static string? ReadRegisteredManifestPath(string registrySubKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registrySubKey);
            return key?.GetValue(string.Empty) as string;
        }
        catch
        {
            return null;
        }
    }
}
