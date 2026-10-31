// NOTE: the CoreCLR startup-hook contract requires the entry type to live in the GLOBAL namespace and
// to be called exactly "StartupHook" with a public static void Initialize(). Do not add a namespace here.
using CookieBridge.Core;
using CookieBridge.Hook;

/// <summary>
/// Entry point required by the CoreCLR startup-hook contract.
///
/// The runtime calls <see cref="Initialize"/> inside VRCVideoCacher's own process, before
/// <c>Program.Main</c>. Two rules apply and both are absolute:
///   1. never let an exception escape — a throwing startup hook aborts the host process;
///   2. never block the caller — everything happens on a background thread.
/// </summary>
public static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            BridgeLog.Info("hook", "Startup hook initialised inside the host process.");

            if (File.Exists(BridgePaths.DisabledFlag))
            {
                BridgeLog.Info("hook", "disabled.flag present — plugin stays passive.");
                return;
            }

            var worker = new Thread(HookRuntime.Instance.Run)
            {
                IsBackground = true,
                Name = "CookieBridge.Hook",
            };
            worker.Start();
        }
        catch
        {
            // Absolutely nothing may escape: this method runs before the host's own error handling exists.
        }
    }
}
