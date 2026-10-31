using System.Text.RegularExpressions;
using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

/// <summary>
/// These read the real machine (HKCU\Software\Valve\Steam, libraryfolders.vdf, the Steam install of
/// VRCVideoCacher). Nothing is written: the locator is read-only by design.
/// </summary>
[Collection(BridgeEnvironmentCollection.Name)]
public sealed class VrcvcLocatorTests : BridgeTestBase
{
    private const string ExpectedSteamRoot = @"D:\Steam";

    [Fact]
    public void ReadSteamPath_ReadsTheRegistry()
    {
        var path = VrcvcLocator.ReadSteamPath();

        Assert.NotNull(path);
        Assert.Equal(ExpectedSteamRoot, path, ignoreCase: true);
        Assert.DoesNotContain('/', path);                 // registry stores forward slashes, we normalise
    }

    [Fact]
    public void ReadLibraryFolders_ContainsTheMainLibrary()
    {
        var libraries = VrcvcLocator.ReadLibraryFolders();

        Assert.NotEmpty(libraries);
        Assert.Contains(libraries, l => string.Equals(l, ExpectedSteamRoot, StringComparison.OrdinalIgnoreCase));
        Assert.All(libraries, l => Assert.True(Directory.Exists(l), $"library path vanished: {l}"));
    }

    [Fact]
    public void SteamLibraries_StartsWithTheSteamRoot()
    {
        var libraries = VrcvcLocator.SteamLibraries();

        Assert.NotEmpty(libraries);
        Assert.Equal(ExpectedSteamRoot, libraries[0], ignoreCase: true);
    }

    [Fact]
    public void Discover_FindsTheInstalledVrcvc()
    {
        var installs = VrcvcLocator.Discover();

        Assert.NotEmpty(installs);
        var install = installs.FirstOrDefault(i =>
            string.Equals(i.InstallDir, @"D:\Steam\steamapps\common\VRCVideoCacher", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(install);
        Assert.True(install!.LooksLikeSteamInstall);
        Assert.True(install.Exists, install.ExePath);
        Assert.Equal(VrcvcLocator.ExeName, Path.GetFileName(install.ExePath));
        Assert.True(File.Exists(install.ExePath));
    }

    [Fact]
    public void Discover_HonoursAUserOverride()
    {
        var fake = Path.Combine(Root, "fake-install");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, VrcvcLocator.ExeName), "stub");

        var installs = VrcvcLocator.Discover(fake);

        Assert.Contains(installs, i => string.Equals(i.DiscoverySource, "user-override", StringComparison.Ordinal));
        Assert.True(VrcvcLocator.Validate(fake));
    }

    [Fact]
    public void Validate_RejectsADirectoryWithoutTheExe()
    {
        Assert.False(VrcvcLocator.Validate(Path.Combine(Root, "empty")));
        Assert.False(VrcvcLocator.Validate("not a path: \0"));
    }

    [Fact]
    public void DetectVersion_ReadsARealVersionFromTheInstall()
    {
        var info = VrcvcLocator.DetectVersion(@"D:\Steam\steamapps\common\VRCVideoCacher");

        Assert.True(info.Known, $"no version found (source={info.Source})");
        Assert.Matches(@"^\d{4}\.\d", info.Version!);
    }

    [Fact]
    public void DetectVersion_PrefersTheRunningProcessVersion()
    {
        var info = VrcvcLocator.DetectVersion(null, "2026.10.29");

        Assert.True(info.Known);
        Assert.True(info.FromRunningProcess);
        Assert.Equal("2026.10.29", info.Version);
    }

    [Fact]
    public void DetectVersion_UnknownWhenNothingIsAvailable()
    {
        var info = VrcvcLocator.DetectVersion(Path.Combine(Root, "nothing-here"));

        Assert.False(info.Known);
        Assert.Equal("unknown", info.Source);
    }

    [Fact]
    public void ProcessState_DoesNotThrow()
    {
        var running = VrcvcProcessState.IsRunning();
        var pid = VrcvcProcessState.RunningPid();

        Assert.Equal(running, pid != null);
    }
}
