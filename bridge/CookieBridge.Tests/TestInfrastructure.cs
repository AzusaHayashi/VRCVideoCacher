using System.Reflection;
using System.Text.Json;
using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

/// <summary>
/// Every test class that touches environment variables / the vault joins this collection. The collection
/// is declared with <c>DisableParallelization</c> and xunit.runner.json also sets
/// <c>parallelizeTestCollections=false</c>, because VRCCB_ROOT / VRCCB_TARGET_FILE are process-wide and
/// parallel collections would cross-contaminate each other (and could write a real user vault).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BridgeEnvironmentCollection
{
    public const string Name = "cookiebridge-env";
}

/// <summary>
/// <c>BridgePaths.Root</c> and <c>BridgePaths.VrcvcDataDir</c> are memoised in private static fields, so
/// changing VRCCB_ROOT after the first read has no effect. Reflection is used here on purpose: production
/// code is not allowed to be modified for the sake of tests.
/// </summary>
internal static class BridgePathsCache
{
    private static readonly FieldInfo? RootField =
        typeof(BridgePaths).GetField("_root", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly FieldInfo? DataField =
        typeof(BridgePaths).GetField("_vrcvcDataDir", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>Drop the memoised paths so the next access re-reads the environment variables.</summary>
    public static void Reset()
    {
        RootField?.SetValue(null, null);
        DataField?.SetValue(null, null);

        // Force an immediate re-resolve so a stale value can never leak into an assertion.
        _ = BridgePaths.Root;
        _ = BridgePaths.VrcvcDataDir;
    }

    public static bool ReflectionAvailable => RootField != null && DataField != null;
}

/// <summary>
/// Per-test isolation fixture: fresh temp root, VRCCB_ROOT / VRCCB_TARGET_FILE / VRCCB_VRCVC_DATA pointed
/// at it, previous values restored on dispose. The real %LOCALAPPDATA%\VRCVideoCacher-CookieBridge is never
/// touched.
/// </summary>
public abstract class BridgeTestBase : IDisposable
{
    private readonly string? _previousRoot;
    private readonly string? _previousTarget;
    private readonly string? _previousVrcvcData;
    private readonly IUserEnvironmentStore _previousStore;

    protected string Root { get; }

    protected string TargetFile { get; }

    protected string VrcvcDataDir { get; }

    protected BridgeTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "cookiebridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        VrcvcDataDir = Path.Combine(Root, "vrcvc-data");
        TargetFile = Path.Combine(VrcvcDataDir, "youtube_cookies.txt");

        _previousRoot = Environment.GetEnvironmentVariable(BridgePaths.RootOverrideVariable);
        _previousTarget = Environment.GetEnvironmentVariable(CookieTargetWriter.TargetOverrideVariable);
        _previousVrcvcData = Environment.GetEnvironmentVariable(BridgePaths.VrcvcDataOverrideVariable);
        _previousStore = HookInstallation.Store;

        Environment.SetEnvironmentVariable(BridgePaths.RootOverrideVariable, Root);
        Environment.SetEnvironmentVariable(CookieTargetWriter.TargetOverrideVariable, TargetFile);
        Environment.SetEnvironmentVariable(BridgePaths.VrcvcDataOverrideVariable, VrcvcDataDir);

        BridgePathsCache.Reset();

        Directory.CreateDirectory(BridgePaths.StateDir);
        Directory.CreateDirectory(BridgePaths.LogDir);
    }

    /// <summary>Guard used by tests that must prove they are not operating on the real user profile.</summary>
    protected void AssertIsolated()
    {
        Assert.StartsWith(Path.GetTempPath(), BridgePaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Root, BridgePaths.Root);
        Assert.Equal(TargetFile, CookieTargetWriter.TargetPath);
    }

    public virtual void Dispose()
    {
        HookInstallation.Store = _previousStore;

        Environment.SetEnvironmentVariable(BridgePaths.RootOverrideVariable, _previousRoot);
        Environment.SetEnvironmentVariable(CookieTargetWriter.TargetOverrideVariable, _previousTarget);
        Environment.SetEnvironmentVariable(BridgePaths.VrcvcDataOverrideVariable, _previousVrcvcData);
        BridgePathsCache.Reset();

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // temp cleanup is best effort
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>In-memory <see cref="IUserEnvironmentStore"/> — the registry is never written by tests.</summary>
public sealed class MemoryEnvironmentStore : IUserEnvironmentStore
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public MemoryEnvironmentStore(params (string Name, string? Value)[] seed)
    {
        foreach (var (name, value) in seed) _values[name] = value;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public void Set(string name, string? value)
    {
        _values[name] = value;
        Writes++;
    }

    public int Writes { get; private set; }
}

/// <summary>Fake cookie jars. Nothing here is a real credential.</summary>
internal static class TestJars
{
    public const long FutureExpiry = 1893456000;   // 2030-01-01
    public const long LaterExpiry = 1924992000;    // 2031-01-01
    public const long ExpiredExpiry = 100;         // 1970: always in the past
    public const string LoginValue = "FAKE_LOGIN_INFO_VALUE_do_not_use";
    public const string SecondLoginValue = "FAKE_SECOND_LOGIN_INFO_value";

    public static string Row(string domain, string name, string value, string path = "/", long expires = FutureExpiry)
        => string.Join('\t', domain, "TRUE", path, "TRUE", expires.ToString(), name, value);

    /// <summary>A jar that satisfies upstream <c>Program.IsCookiesValid</c>.</summary>
    public static string Build(int extraCookies = 3, string loginValue = LoginValue, string prefix = "FAKE_COOKIE")
    {
        var sb = new System.Text.StringBuilder(NetscapeCookieFile.Header);
        sb.Append(Row(".youtube.com", "LOGIN_INFO", loginValue)).Append('\n');
        sb.Append(Row(".youtube.com", "SID", "FAKE_SID_VALUE")).Append('\n');
        sb.Append(Row(".youtube.com", "HSID", "FAKE_HSID_VALUE")).Append('\n');
        for (var i = 0; i < extraCookies; i++)
            sb.Append(Row(".youtube.com", $"{prefix}_{i}", $"FAKE_VALUE_{i}")).Append('\n');
        return sb.ToString();
    }

    /// <summary>A jar with a youtube.com cookie but no LOGIN_INFO — upstream rejects it.</summary>
    public static string BuildWithoutLogin()
    {
        var sb = new System.Text.StringBuilder(NetscapeCookieFile.Header);
        sb.Append(Row(".youtube.com", "PREF", "FAKE_PREF_VALUE")).Append('\n');
        sb.Append(Row(".youtube.com", "YSC", "FAKE_YSC_VALUE")).Append('\n');
        return sb.ToString();
    }

    /// <summary>A jar for a different host — upstream rejects it too.</summary>
    public static string BuildForeignHost()
    {
        var sb = new System.Text.StringBuilder(NetscapeCookieFile.Header);
        sb.Append(Row(".example.com", "LOGIN_INFO", LoginValue)).Append('\n');
        return sb.ToString();
    }
}
