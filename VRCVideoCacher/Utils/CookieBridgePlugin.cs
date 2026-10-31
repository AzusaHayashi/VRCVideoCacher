using System.Reflection;

namespace VRCVideoCacher.Utils;

/// <summary>
/// CookieBridge integration point (this fork only).
///
/// Loads the optional CookieBridge plugin from the per-user bridge directory, if the user installed it
/// through the CookieBridge manager. The plugin then feeds the newest YouTube cookie jar — produced by
/// the browser extension — into VRCVideoCacher's existing cookie mechanism
/// (<see cref="YTDL.YtdlManager.CookiesPath"/>), using <see cref="YTDL.YtdlCookieJar"/> for serialisation
/// and <c>Program.NotifyCookiesUpdated</c> to refresh the UI.
///
/// Design rules, so this stays mergeable with upstream and cannot break a normal build:
///  * no compile-time reference to the plugin (loaded by path, by reflection);
///  * no behaviour change when the plugin is absent, disabled, or throws;
///  * everything is wrapped — a broken plugin must never stop VRCVideoCacher from starting.
///
/// The Steam build ships as a single-file publish with the CoreCLR startup-hook feature switch turned
/// off, so the DOTNET_STARTUP_HOOKS mechanism cannot reach it; a build from this fork calls this method
/// directly instead. See docs/ARCHITECTURE.md.
/// </summary>
internal static class CookieBridgePlugin
{
    public const string BridgeFolderName = "VRCVideoCacher-CookieBridge";
    public const string PluginAssemblyName = "CookieBridge.Hook.dll";

    private static bool _attempted;

    public static void TryLoad()
    {
        if (_attempted) return;
        _attempted = true;

        try
        {
            var bridgeRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BridgeFolderName);

            if (File.Exists(Path.Combine(bridgeRoot, "disabled.flag")))
                return;

            var candidates = new[]
            {
                // Shipped next to the executable (self-contained distribution).
                Path.Combine(AppContext.BaseDirectory, PluginAssemblyName),
                // Installed by the CookieBridge manager (per-user, survives Steam updates).
                Path.Combine(bridgeRoot, "hook", PluginAssemblyName),
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null) return;

            var assembly = Assembly.LoadFrom(path);
            var initialize = assembly.GetType("StartupHook", throwOnError: false)
                ?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);

            initialize?.Invoke(null, null);
        }
        catch
        {
            // A plugin that cannot load must be indistinguishable from no plugin at all.
        }
    }
}
