using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class HookInstallationTests : BridgeTestBase
{
    private const string ForeignHook = @"C:\other-tools\dotnet-counters\probe.dll";

    private string HookDll => Path.Combine(BridgePaths.HookDir, HookInstallation.HookAssemblyName);

    public HookInstallationTests()
    {
        // Never touch HKCU\Environment.
        HookInstallation.Store = new MemoryEnvironmentStore();
    }

    private static string[] Entries(string? value) =>
        (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void InjectedStore_IsUsed_NotTheRegistry()
    {
        Assert.IsType<MemoryEnvironmentStore>(HookInstallation.Store);
        Assert.Null(HookInstallation.ReadUserEnv());
    }

    [Fact]
    public void RegisterEnv_PreservesForeignEntries()
    {
        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        store.Set(BridgePaths.StartupHooksVariable, ForeignHook);

        Assert.True(HookInstallation.RegisterEnv(HookDll, out var message), message);

        var entries = Entries(store.Get(BridgePaths.StartupHooksVariable));
        Assert.Contains(ForeignHook, entries);
        Assert.Contains(HookDll, entries);
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public void RegisterEnv_IsIdempotent()
    {
        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        store.Set(BridgePaths.StartupHooksVariable, ForeignHook);

        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));
        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));
        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));

        var entries = Entries(store.Get(BridgePaths.StartupHooksVariable));
        Assert.Equal(1, entries.Count(e => string.Equals(e, HookDll, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, entries.Count(e => string.Equals(e, ForeignHook, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public void RegisterEnv_RemovesDuplicatesWhenTheSameHookIsAlreadyPresentTwice()
    {
        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        store.Set(BridgePaths.StartupHooksVariable, $"{HookDll};{ForeignHook};{HookDll}");

        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));

        var entries = Entries(store.Get(BridgePaths.StartupHooksVariable));
        Assert.Equal(1, entries.Count(e => string.Equals(e, HookDll, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(ForeignHook, entries[0]);
    }

    [Fact]
    public void RemoveEnv_DeletesOnlyOurEntries()
    {
        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));
        store.Set(BridgePaths.StartupHooksVariable,
            $"{ForeignHook};{store.Get(BridgePaths.StartupHooksVariable)}");

        Assert.True(HookInstallation.RemoveEnv(out var message), message);

        var entries = Entries(store.Get(BridgePaths.StartupHooksVariable));
        Assert.Single(entries);
        Assert.Equal(ForeignHook, entries[0]);
    }

    [Fact]
    public void RemoveEnv_ClearsTheVariableWhenNothingElseRemains()
    {
        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        Assert.True(HookInstallation.RegisterEnv(HookDll, out _));

        Assert.True(HookInstallation.RemoveEnv(out _));

        Assert.Null(store.Get(BridgePaths.StartupHooksVariable));
    }

    [Fact]
    public void RemoveEnv_IsSafeWhenVariableIsAlreadyEmpty()
    {
        Assert.True(HookInstallation.RemoveEnv(out _));
        Assert.Null(HookInstallation.ReadUserEnv());
    }

    [Fact]
    public void Inspect_ReportsMissingDllAndMissingEnv()
    {
        var state = HookInstallation.Inspect();

        Assert.False(state.DllPresent);
        Assert.False(state.Installed);
        Assert.False(state.EnvRegistered);
        Assert.True(state.Enabled);
        Assert.NotEmpty(state.Problems);
    }

    [Fact]
    public void Install_CopiesTheDll_RegistersEnv_AndWritesManifest()
    {
        AssertIsolated();

        var source = Path.Combine(Root, "source", HookInstallation.HookAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "%fake shell of the hook dll%");

        Assert.True(HookInstallation.Install(source, out var message, registerStartupHook: true), message);

        Assert.True(File.Exists(HookDll));
        Assert.True(File.Exists(BridgePaths.InstallManifest));
        Assert.True(HookInstallation.IsEnabled);

        var manifest = HookInstallation.ReadManifest();
        Assert.NotNull(manifest);
        Assert.Equal(HookInstallation.Sha256File(HookDll), manifest!.Sha256);

        var state = HookInstallation.Inspect();
        Assert.True(state.DllPresent);
        Assert.True(state.Installed);
        Assert.True(state.EnvRegistered);
        Assert.True(state.DllHashMatches);
    }

    [Fact]
    public void SetEnabled_TogglesTheDisabledFlag()
    {
        AssertIsolated();

        HookInstallation.SetEnabled(false);
        Assert.False(HookInstallation.IsEnabled);
        Assert.True(File.Exists(BridgePaths.DisabledFlag));

        HookInstallation.SetEnabled(true);
        Assert.True(HookInstallation.IsEnabled);
        Assert.False(File.Exists(BridgePaths.DisabledFlag));
    }

    [Fact]
    public void Install_ByDefaultDoesNotTouchStartupHooks()
    {
        AssertIsolated();

        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        store.Set(BridgePaths.StartupHooksVariable, ForeignHook);

        var source = Path.Combine(Root, "source", HookInstallation.HookAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "%fake shell of the hook dll%");

        // The Steam build disables the startup-hook feature switch, so registration is opt-in on purpose.
        Assert.True(HookInstallation.Install(source, out var message), message);

        Assert.Equal(ForeignHook, store.Get(BridgePaths.StartupHooksVariable));
        Assert.True(File.Exists(HookDll));
        Assert.True(HookInstallation.Inspect().DllPresent);
        Assert.False(HookInstallation.Inspect().EnvRegistered);
        Assert.Empty(HookInstallation.ReadManifest()!.EnvEntriesAdded);
    }

    [Fact]
    public void Install_RejectsMissingSource()
    {
        Assert.False(HookInstallation.Install(Path.Combine(Root, "missing.dll"), out var message));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Uninstall_RemovesDllManifestAndEnvButKeepsForeignHooks()
    {
        AssertIsolated();

        var store = (MemoryEnvironmentStore)HookInstallation.Store;
        store.Set(BridgePaths.StartupHooksVariable, ForeignHook);

        var source = Path.Combine(Root, "source", HookInstallation.HookAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "%fake shell of the hook dll%");
        Assert.True(HookInstallation.Install(source, out var message, registerStartupHook: true), message);

        Assert.True(HookInstallation.Uninstall(out var uninstallMessage), uninstallMessage);

        Assert.False(File.Exists(HookDll));
        Assert.False(File.Exists(BridgePaths.InstallManifest));
        var entries = Entries(store.Get(BridgePaths.StartupHooksVariable));
        Assert.Single(entries);
        Assert.Equal(ForeignHook, entries[0]);
    }
}
