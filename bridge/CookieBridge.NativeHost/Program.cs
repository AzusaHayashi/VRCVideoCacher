using System.Text;
using CookieBridge.Core;

namespace CookieBridge.NativeHost;

/// <summary>
/// Two modes in one binary:
///
///  * default (no arguments) — native-messaging host spawned by Chrome/Firefox. Contract: 4-byte
///    little-endian length + UTF-8 JSON on stdin, same framing back on stdout. Nothing else may be
///    written to stdout (a stray WriteLine corrupts the channel), so diagnostics go to the bridge log.
///
///  * CLI (--register-host / --unregister-host / --status / --send ...) — used by the installer, the
///    manager and the automated tests. Same code paths as the browser, no extra tooling.
/// </summary>
internal static class Program
{
    private const string ChromeExtensionId = "fdjmcdfolkgicljalacnlemepfhmnmfp";
    private const string FirefoxExtensionId = "vrexcookiebridge@local";

    private static int Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? RunNativeMessaging() : RunCli(args);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("host", "Unhandled failure", ex);
            return 1;
        }
    }

    private static int RunNativeMessaging()
    {
        var stdout = Console.OpenStandardOutput();
        BridgeLog.Info("host", "Native messaging host started.");

        var stdin = Console.OpenStandardInput();
        while (true)
        {
            var message = NativeMessageProtocol.ReadMessage(stdin);
            if (message == null) break;

            NativeMessageResponse response;
            try
            {
                response = NativeMessageHandler.HandleJson(message);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("host", "Unhandled error while processing a native message", ex);
                response = new NativeMessageResponse { Ok = false, Error = "internal-error" };
            }

            NativeMessageProtocol.WriteMessage(stdout, BridgeJson.Serialize(response, compact: true));
        }

        BridgeLog.Info("host", "Native messaging host finished.");
        return 0;
    }

    private static int RunCli(string[] args)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "--register-host":
            case "--install":
                return Register(args);

            case "--unregister-host":
            case "--uninstall":
                return Unregister();

            case "--status":
                return PrintStatus();

            case "--send":
                return Send(args);

            case "--version":
                Console.WriteLine(typeof(Program).Assembly.GetName().Version);
                return 0;

            default:
                Console.Error.WriteLine(
                    "usage: CookieBridge.NativeHost [--register-host|--unregister-host|--status|--send <file>|--version]");
                return 2;
        }
    }

    private static int Register(string[] args)
    {
        var chromium = ValueOf(args, "--chromium-id") ?? ChromeExtensionId;
        var firefox = ValueOf(args, "--firefox-id") ?? FirefoxExtensionId;
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CookieBridge.NativeHost.exe");

        var ok = NativeHostRegistration.Register(exePath, [chromium], [firefox], out var message);
        Console.WriteLine(message);
        Console.WriteLine($"chromium manifest: {NativeHostRegistration.ChromiumManifestPath}");
        Console.WriteLine($"firefox  manifest: {NativeHostRegistration.FirefoxManifestPath}");
        Console.WriteLine($"chromium extension origin: chrome-extension://{chromium}/");
        return ok ? 0 : 1;
    }

    private static int Unregister()
    {
        var ok = NativeHostRegistration.Unregister(out var message);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static int PrintStatus()
    {
        var vault = CookieVault.Load();
        var target = CookieTargetWriter.ReadState();
        var hook = HookStatus.Read();
        var compat = CompatibilityMatrix.Evaluate(VrcvcLocator.DetectVersion().Version);

        Console.WriteLine($"bridge root      : {BridgePaths.Root}");
        Console.WriteLine($"vault            : {(vault.Success ? "ok" : vault.Error)} " +
                          $"({vault.Summary.CookieCount} cookies, login={vault.Summary.LoginCookiePresent}, " +
                          $"updated={vault.UpdatedUtc:O})");
        Console.WriteLine($"cookie file      : {CookieTargetWriter.TargetPath}");
        Console.WriteLine($"  exists={target.Exists} valid={target.Valid} cookies={target.CookieCount} " +
                          $"written={target.LastWriteUtc:O}");
        Console.WriteLine($"vrcvc            : version={compat.Version ?? "unknown"} ({compat.LevelLabel}), running={VrcvcProcessState.IsRunning()}");
        Console.WriteLine($"in-process hook  : {(hook is { IsFresh: true } ? $"live (pid {hook.Pid})" : "not loaded")}");
        Console.WriteLine($"native host reg  : {string.Join(", ", NativeHostRegistration.GetRegisteredTargets().Select(t => t.DisplayName))}");
        return vault.Success ? 0 : 3;
    }

    /// <summary>
    /// Sends a cookie.update through the exact same handler the browser exercises.
    /// <paramref name="args"/>[1] = path to a Netscape cookie file, or "-" to read stdin (UTF-8).
    /// </summary>
    private static int Send(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "-";
        var cookies = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);

        var request = new CookieUpdateRequest
        {
            Type = NativeMessageHandler.TypeCookieUpdate,
            Browser = "cli",
            ExtensionVersion = "cli",
            Cookies = cookies,
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        var response = NativeMessageHandler.HandleJson(BridgeJson.Serialize(request));
        Console.WriteLine(BridgeJson.Serialize(response));
        return response.Ok ? 0 : 1;
    }

    private static string? ValueOf(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
