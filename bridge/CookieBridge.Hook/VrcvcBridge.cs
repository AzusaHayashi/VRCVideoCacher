using System.Reflection;
using CookieBridge.Core;

namespace CookieBridge.Hook;

/// <summary>
/// The compatibility layer between this plugin and VRCVideoCacher internals.
///
/// Everything is reflection over *public* members that the application itself uses, so there are no
/// offsets, no IL patching, no Harmony and no recompilation per release. Each capability is probed
/// independently; a missing member downgrades one layer instead of breaking the plugin.
/// </summary>
internal sealed class VrcvcBridge
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private MethodInfo? _acquireAsync;
    private MethodInfo? _notifyCookiesUpdated;
    private MethodInfo? _isCookiesValid;
    private EventInfo? _cookieEvent;

    public string CookiePath { get; private set; } = BridgePaths.VrcvcCookieFile;
    public string? Version { get; private set; }
    public string AssemblyName { get; private set; } = "unknown";
    public bool HasJarGate => _acquireAsync != null;
    public bool HasNotify => _notifyCookiesUpdated != null;
    public bool HasValidCheck => _isCookiesValid != null;
    public Dictionary<string, string> Layers { get; } = new();

    public static VrcvcBridge? TryCreate(Assembly assembly)
    {
        try
        {
            var program = assembly.GetType("VRCVideoCacher.Program", throwOnError: false);
            if (program == null) return null;

            var bridge = new VrcvcBridge { AssemblyName = assembly.GetName().Name ?? "unknown" };

            // L1 — cookie jar path. Contract-level capability with a hard fallback.
            try
            {
                var ytdlManager = assembly.GetType("VRCVideoCacher.YTDL.YtdlManager", throwOnError: false);
                if (ytdlManager?.GetField("CookiesPath", AnyStatic)?.GetValue(null) is string path && path.Length > 0)
                {
                    bridge.CookiePath = path;
                    bridge.Layers["L1.cookiePath"] = "resolved";
                }
                else
                {
                    bridge.Layers["L1.cookiePath"] = "fallback";
                }
            }
            catch (Exception ex)
            {
                bridge.Layers["L1.cookiePath"] = $"fallback({ex.GetType().Name})";
            }

            // L2 — VRCVideoCacher's own yt-dlp cookie-jar semaphore (prevents lost token rotation).
            try
            {
                var jar = assembly.GetType("VRCVideoCacher.YTDL.YtdlCookieJar", throwOnError: false);
                bridge._acquireAsync =
                    jar?.GetMethod("AcquireAsync", AnyStatic, null, [typeof(CancellationToken)], null)
                    ?? jar?.GetMethod("AcquireAsync", AnyStatic);
                bridge.Layers["L2.jarGate"] = bridge._acquireAsync != null ? "resolved" : "absent";
            }
            catch (Exception ex)
            {
                bridge.Layers["L2.jarGate"] = $"absent({ex.GetType().Name})";
            }

            // L3 — UI refresh notification.
            try
            {
                bridge._notifyCookiesUpdated =
                    program.GetMethod("NotifyCookiesUpdated", AnyStatic, null, Type.EmptyTypes, null);
                bridge.Layers["L3.notify"] = bridge._notifyCookiesUpdated != null ? "resolved" : "absent";
            }
            catch (Exception ex)
            {
                bridge.Layers["L3.notify"] = $"absent({ex.GetType().Name})";
            }

            // L4 — VRCVideoCacher's own validity rule.
            try
            {
                bridge._isCookiesValid =
                    program.GetMethod("IsCookiesValid", AnyStatic, null, [typeof(string)], null);
                bridge.Layers["L4.validity"] = bridge._isCookiesValid != null ? "resolved" : "absent";
            }
            catch (Exception ex)
            {
                bridge.Layers["L4.validity"] = $"absent({ex.GetType().Name})";
            }

            // L5 — version string.
            try
            {
                var raw = program.GetField("Version", AnyStatic)?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(raw))
                    raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                bridge.Version = string.IsNullOrWhiteSpace(raw) ? null : raw.Split('+')[0].Trim();
                bridge.Layers["L5.version"] = bridge.Version != null ? "resolved" : "absent";
            }
            catch (Exception ex)
            {
                bridge.Layers["L5.version"] = $"absent({ex.GetType().Name})";
            }

            // L6 — observe cookies arriving through VRCVideoCacher's own HTTP API (informational).
            try
            {
                bridge._cookieEvent = program.GetEvent("OnCookiesUpdated", AnyStatic);
                bridge.Layers["L6.cookieEvent"] = bridge._cookieEvent != null ? "resolved" : "absent";
            }
            catch
            {
                bridge.Layers["L6.cookieEvent"] = "absent";
            }

            return bridge;
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("reflect", $"Bridge creation failed: {ex.GetType().Name}");
            return null;
        }
    }

    public void SubscribeCookieUpdates(Action onUpdated)
    {
        if (_cookieEvent?.EventHandlerType == null) return;
        try
        {
            var handler = Delegate.CreateDelegate(_cookieEvent.EventHandlerType, onUpdated.Target, onUpdated.Method);
            _cookieEvent.AddEventHandler(null, handler);
            BridgeLog.Info("reflect", "Subscribed to VRCVideoCacher's OnCookiesUpdated event.");
        }
        catch (Exception ex)
        {
            BridgeLog.Debug("reflect", $"Event subscription skipped: {ex.GetType().Name}");
        }
    }

    /// <summary>Acquire VRCVideoCacher's own yt-dlp gate. Returns null when unavailable.</summary>
    public async Task<IDisposable?> AcquireJarAsync(CancellationToken ct)
    {
        if (_acquireAsync == null) return null;
        try
        {
            var result = _acquireAsync.Invoke(null, [ct]);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result")?.GetValue(task) as IDisposable;
            }
            return result as IDisposable;
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("reflect", $"YtdlCookieJar.AcquireAsync failed: {ex.GetType().Name}");
            return null;
        }
    }

    public void NotifyCookiesUpdated()
    {
        if (_notifyCookiesUpdated == null) return;
        try
        {
            _notifyCookiesUpdated.Invoke(null, null);
        }
        catch (Exception ex)
        {
            BridgeLog.Debug("reflect", $"NotifyCookiesUpdated threw: {ex.GetType().Name}");
        }
    }

    public bool IsCookiesValid(string text)
    {
        if (_isCookiesValid == null) return NetscapeCookieFile.IsValidForVrcvc(text);
        try
        {
            return _isCookiesValid.Invoke(null, [text]) is true;
        }
        catch
        {
            return NetscapeCookieFile.IsValidForVrcvc(text);
        }
    }

    public string DescribeLayers() =>
        string.Join(", ", Layers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
}
