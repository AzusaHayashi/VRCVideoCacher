using System.Globalization;
using System.Reflection;
using VRCVideoCacher.YTDL;

namespace VRCVideoCacher;

/// <summary>
/// Stand-in for the real VRCVideoCacher host, used by the integration tests.
///
/// It deliberately mirrors the *shape* the hook depends on — not the implementation:
///   * namespace/type/member names are identical, so <c>VrcvcBridge</c> resolves every layer (L1..L6);
///   * it is published single-file + self-contained + trimmed, exactly like the shipping Release build,
///     so the <c>DOTNET_STARTUP_HOOKS</c> + reflection path is exercised against the real publishing shape;
///   * its <c>Version</c> (2099.1.1) is newer than the compatibility matrix's verified version, which also
///     proves an unknown/newer host is still allowed to run (docs/CONTRACT.md §9).
///
/// It does not stream video, does not touch the network and never writes the cookie file itself.
/// </summary>
internal sealed class Program
{
    /// <summary>Newer than <c>CompatibilityMatrix.VerifiedVersion</c> on purpose.</summary>
    public static readonly string Version = "2099.1.1";

    public static readonly string DataPath =
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCVideoCacher");

    public static readonly string CurrentProcessPath = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;

    /// <summary>Same event contract as upstream: the hook subscribes to it reflectively (L6).</summary>
    public static event Action? OnCookiesUpdated;

    /// <summary>Same rule as upstream <c>Program.IsCookiesValid</c> (L4).</summary>
    public static bool IsCookiesValid(string cookies)
    {
        if (string.IsNullOrEmpty(cookies)) return false;
        return cookies.Contains("youtube.com") && cookies.Contains("LOGIN_INFO");
    }

    /// <summary>Same notification contract as upstream (L3).</summary>
    public static void NotifyCookiesUpdated()
    {
        try
        {
            OnCookiesUpdated?.Invoke();
            Console.WriteLine("MOCKHOST notify OnCookiesUpdated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MOCKHOST notify failed: {ex.GetType().Name}");
        }
    }

    private static int Main(string[] args)
    {
        var duration = TimeSpan.FromSeconds(30);
        foreach (var arg in args)
        {
            if (arg.StartsWith("--duration=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg["--duration=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }
        }

        // Touch every reflective entry point from real code so PublishTrimmed can never drop them.
        _ = IsCookiesValid(".youtube.com\tTRUE\t/\tTRUE\t0\tLOGIN_INFO\tprobe");
        NotifyCookiesUpdated();
        using (var probeGate = YtdlCookieJar.AcquireAsync().GetAwaiter().GetResult())
        {
            _ = probeGate;
        }

        var informationalVersion =
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            "unknown";

        Console.WriteLine(
            $"MOCKHOST ready pid={Environment.ProcessId} version={Version} informationalVersion={informationalVersion} " +
            $"cookiePath={YtdlManager.CookiesPath} startupHooks={Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS")}");

        var deadline = DateTime.UtcNow + duration;
        var lastSignature = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var (count, login, exists) = Inspect(YtdlManager.CookiesPath);
            var signature = $"{exists}:{count}:{login}";

            Console.WriteLine(
                $"MOCKHOST poll pid={Environment.ProcessId} exists={exists} count={count} login={login} " +
                $"valid={IsCookiesValid(ReadAll(YtdlManager.CookiesPath))}");

            if (!string.Equals(signature, lastSignature, StringComparison.Ordinal))
            {
                Console.WriteLine($"MOCKHOST changed exists={exists} count={count} login={login}");
                lastSignature = signature;
            }

            Console.Out.Flush();
            Thread.Sleep(500);
        }

        Console.WriteLine($"MOCKHOST exiting pid={Environment.ProcessId}");
        Console.Out.Flush();
        return 0;
    }

    private static string ReadAll(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Cookie count / LOGIN_INFO presence, computed exactly like yt-dlp's Netscape parser.</summary>
    private static (int Count, bool Login, bool Exists) Inspect(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, false, false);

            var count = 0;
            var login = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
                else if (line.StartsWith('#')) continue;

                var parts = line.Split('\t');
                if (parts.Length < 7) continue;

                count++;
                if (string.Equals(parts[5], "LOGIN_INFO", StringComparison.Ordinal)) login = true;
            }

            return (count, login, true);
        }
        catch
        {
            return (0, false, false);
        }
    }
}
