using CookieBridge.Core;
using CookieBridge.Hook;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class CookieMergerTests : BridgeTestBase
{
    private static string Row(string name, string value, string path = "/")
        => TestJars.Row(".youtube.com", name, value, path);

    private static Dictionary<string, string> Values(string merged) =>
        NetscapeCookieFile.Parse(merged).ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);

    [Fact]
    public void SameLoginInfo_KeepsTargetOnlyCookies_AndBrowserOverwritesSharedOnes()
    {
        var vault = NetscapeCookieFile.Header +
                    Row("LOGIN_INFO", "SAME_SESSION") + "\n" +
                    Row("PREF", "browser-value") + "\n";

        // yt-dlp rotated SIDCC and added a cookie the browser never sends; PREF is stale here.
        var target = NetscapeCookieFile.Header +
                     Row("LOGIN_INFO", "SAME_SESSION") + "\n" +
                     Row("PREF", "stale-value") + "\n" +
                     Row("SIDCC", "rotated-value") + "\n" +
                     Row("EXTRA", "target-only") + "\n";

        var merged = CookieMerger.Merge(vault, target);
        var values = Values(merged);

        // target-only cookie survives
        Assert.True(values.ContainsKey("EXTRA"));
        Assert.Equal("target-only", values["EXTRA"]);

        // yt-dlp rotation survives (browser never sent SIDCC)
        Assert.True(values.ContainsKey("SIDCC"));
        Assert.Equal("rotated-value", values["SIDCC"]);

        // browser value wins for a shared cookie
        Assert.Equal("browser-value", values["PREF"]);

        Assert.Equal(4, NetscapeCookieFile.Parse(merged).Count);
        Assert.StartsWith(NetscapeCookieFile.Header, merged, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentLoginInfo_VaultWinsCompletely()
    {
        var vault = NetscapeCookieFile.Header +
                    Row("LOGIN_INFO", TestJars.SecondLoginValue) + "\n" +
                    Row("PREF", "browser-value") + "\n";

        var target = NetscapeCookieFile.Header +
                     Row("LOGIN_INFO", TestJars.LoginValue) + "\n" +
                     Row("SIDCC", "rotated-value") + "\n" +
                     Row("EXTRA", "target-only") + "\n";

        var merged = CookieMerger.Merge(vault, target);
        var values = Values(merged);

        Assert.Equal(NetscapeCookieFile.Canonicalize(vault), merged);
        Assert.False(values.ContainsKey("EXTRA"));
        Assert.False(values.ContainsKey("SIDCC"));
        Assert.Equal(TestJars.SecondLoginValue, values["LOGIN_INFO"]);
    }

    [Fact]
    public void NullTarget_ReturnsCanonicalVault()
    {
        var vault = TestJars.Build();

        Assert.Equal(NetscapeCookieFile.Canonicalize(vault), CookieMerger.Merge(vault, null));
        Assert.Equal(NetscapeCookieFile.Canonicalize(vault), CookieMerger.Merge(vault, ""));
        Assert.Equal(NetscapeCookieFile.Canonicalize(vault), CookieMerger.Merge(vault, "   \n"));
    }

    [Fact]
    public void TargetWithoutLoginInfo_VaultWins()
    {
        var vault = TestJars.Build();

        var merged = CookieMerger.Merge(vault, TestJars.BuildWithoutLogin());

        Assert.Equal(NetscapeCookieFile.Canonicalize(vault), merged);
    }

    [Fact]
    public void ResultIsAlwaysVrcvcValid_WhenVaultIsValid()
    {
        var vault = TestJars.Build();

        var merged = CookieMerger.Merge(vault, TestJars.BuildWithoutLogin());

        Assert.True(NetscapeCookieFile.IsValidForVrcvc(merged));
        Assert.Equal(7, merged.Split('\n').First(l => l.Length > 0 && !l.StartsWith('#')).Split('\t').Length);
    }
}
