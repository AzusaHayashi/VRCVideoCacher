using System.Diagnostics;
using System.IO;
using System.Text;
using CookieBridge.Core;

namespace CookieBridge.Manager.Services;

// =============================================================================================
//  数据传输对象：ManagerService 只产出这些“快照”，UI 只负责显示。全部字段都是非敏感信息
//  （条数 / 布尔 / 时间 / 路径），绝不包含 Cookie 名称或值，见 docs/CONTRACT.md §8。
// =============================================================================================

/// <summary>一次操作的通用结果。</summary>
public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

/// <summary>一个被发现的 VRCVideoCacher 安装候选（含版本与来源）。</summary>
public sealed class VrcvcCandidate
{
    public required string ExePath { get; init; }
    public required string InstallDir { get; init; }
    public required string DiscoverySource { get; init; }
    public required bool LooksLikeSteamInstall { get; init; }
    public string VersionText { get; init; } = "未知";
    public string VersionSource { get; init; } = "unknown";

    public string SourceText => LooksLikeSteamInstall ? $"Steam · {DiscoverySource}" : DiscoverySource;

    /// <summary>ComboBox 里显示的一行。</summary>
    public string Display => $"{InstallDir}    [版本 {VersionText} · 来源 {SourceText}]";

    public static VrcvcCandidate From(VrcvcInstall install)
    {
        var version = SafeVersion(install.InstallDir);
        return new VrcvcCandidate
        {
            ExePath = install.ExePath,
            InstallDir = install.InstallDir,
            DiscoverySource = install.DiscoverySource,
            LooksLikeSteamInstall = install.LooksLikeSteamInstall,
            VersionText = version.Version ?? "未知",
            VersionSource = version.Source,
        };
    }

    private static VrcvcVersionInfo SafeVersion(string dir)
    {
        // 只读 PE 版本号；这里刻意不传 runningAssemblyVersion，避免一个候选的版本污染另一个候选。
        try
        {
            var exe = Path.Combine(dir, VrcvcLocator.ExeName);
            var info = FileVersionInfo.GetVersionInfo(exe);
            foreach (var raw in new[] { info.ProductVersion, info.FileVersion })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var cleaned = raw.Split('+')[0].Trim();
                if (CompatibilityMatrix.Parse(cleaned) != null)
                    return new VrcvcVersionInfo(cleaned, "pe-file-version", false);
            }

            return new VrcvcVersionInfo(null, "pe-file-version", false, "PE 中无 2026.x 形式版本号");
        }
        catch (Exception ex)
        {
            return new VrcvcVersionInfo(null, "pe-file-version", false, ex.GetType().Name);
        }
    }
}

/// <summary>插件（Hook）安装态。</summary>
public sealed record PluginStatus(
    bool Installed,
    bool Enabled,
    bool EnvRegistered,
    bool DllPresent,
    bool DllHashMatches,
    string? DllPath,
    string? Version,
    string? EnvValue,
    string HashShort,
    bool HasManifest,
    DateTimeOffset? InstalledUtc,
    string? ManifestSha,
    string DisabledFlagPath,
    bool DisabledFlagExists,
    string? DisabledFlagContent,
    IReadOnlyList<string> Problems)
{
    public static PluginStatus Empty { get; } = new(
        false, true, false, false, false, null, null, null, "—", false, null, null,
        BridgePaths.DisabledFlag, false, null,
        ["尚未读取状态"]);
}

/// <summary>Cookie 两侧状态：加密 vault + VRCVC 实际消费的目标文件。</summary>
public sealed record CookieStatus(
    bool VaultExists,
    bool VaultReadable,
    int VaultCookieCount,
    bool VaultLogin,
    DateTimeOffset? VaultUpdatedUtc,
    string? VaultProtection,
    string? VaultSource,
    string? VaultError,
    string TargetPath,
    bool TargetExists,
    bool TargetReadable,
    bool TargetValid,
    int TargetCookieCount,
    bool TargetLogin,
    DateTimeOffset? TargetLastWriteUtc,
    long TargetSize,
    string? TargetError)
{
    public static CookieStatus Empty { get; } = new(
        false, false, 0, false, null, null, null, null,
        CookieTargetWriter.TargetPath, false, false, false, 0, false, null, 0, null);
}

/// <summary>Layers 探测表的一行（L1.cookiePath = resolved 之类）。</summary>
public sealed record LayerEntry(string Layer, string Value);

/// <summary>插件在 VRCVC 进程内的运行状态。</summary>
public sealed record LiveStatus(
    bool Fresh,
    int Pid,
    string? VrcvcVersion,
    string? HookVersion,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? HeartbeatUtc,
    DateTimeOffset? LastSyncUtc,
    string? LastSyncOutcome,
    int SyncCount,
    string? LastError,
    bool VaultWatching,
    IReadOnlyList<LayerEntry> Layers,
    bool ProcessRunning,
    int? ProcessPid,
    string? StatusFileError)
{
    public static LiveStatus Empty { get; } = new(
        false, 0, null, null, null, null, null, null, 0, null, false,
        [], false, null, null);
}

/// <summary>浏览器 Native Messaging 注册态。</summary>
public sealed record NativeStatus(
    IReadOnlyList<string> RegisteredBrowsers,
    string ChromiumManifestPath,
    string FirefoxManifestPath,
    string? HostExePath,
    bool HostExeFound,
    IReadOnlyList<string> KnownBrowsers)
{
    public static NativeStatus Empty { get; } = new(
        [], NativeHostRegistration.ChromiumManifestPath, NativeHostRegistration.FirefoxManifestPath,
        null, false, []);
}

/// <summary>Steam 进程与“环境变量是否可能过期”的判定。</summary>
public sealed record SteamStatus(
    bool SteamRunning,
    DateTimeOffset? SteamStartedUtc,
    string? SteamExePath,
    bool EnvMayBeStale,
    DateTimeOffset? InstalledUtc)
{
    public static SteamStatus Empty { get; } = new(false, null, null, false, null);
}

/// <summary>一次完整刷新的结果。</summary>
public sealed record ManagerSnapshot(
    IReadOnlyList<VrcvcCandidate> Candidates,
    VrcvcCandidate? Selected,
    VrcvcVersionInfo Version,
    CompatReport Compat,
    PluginStatus Plugin,
    CookieStatus Cookie,
    LiveStatus Live,
    NativeStatus Native,
    SteamStatus Steam,
    string? ManualDirProblem);

// =============================================================================================
//  ManagerService：所有 CookieBridge.Core 调用的唯一入口。每个方法内部 try/catch，
//  永远返回“结果对象”，不向 UI 抛异常。
// =============================================================================================

public sealed class ManagerService
{
    /// <summary>手动选择的安装目录（用户通过“选择目录”按钮指定）。</summary>
    public string? ManualInstallDir { get; set; }

    /// <summary>当前选中的安装目录（用于版本探测与显示）。</summary>
    public string? SelectedInstallDir { get; private set; }

    /// <summary>用户在候选列表里换了一个安装（不算 user-override，只影响后续读取哪个目录）。</summary>
    public void SelectInstall(string? installDir) => SelectedInstallDir = installDir;

    // ---------------------------------------------------------------- 完整刷新

    public ManagerSnapshot Refresh()
    {
        var candidates = DiscoverCandidates(out var manualProblem);
        var selected = candidates.FirstOrDefault(c =>
                           SelectedInstallDir != null &&
                           string.Equals(c.InstallDir, SelectedInstallDir, StringComparison.OrdinalIgnoreCase))
                       ?? candidates.FirstOrDefault();

        SelectedInstallDir = selected?.InstallDir;

        var version = DetectVersion(selected?.InstallDir);
        var compat = SafeCompat(version);
        var plugin = ReadPluginStatus();
        var cookie = ReadCookieStatus();
        var live = ReadLiveStatus();
        var native = ReadNativeStatus();
        var steam = ReadSteamStatus(plugin.InstalledUtc);
        var extensionId = ReadExtensionIdentity();

        return new ManagerSnapshot(
            candidates,
            selected,
            version,
            compat,
            plugin,
            cookie,
            live,
            native,
            steam,
            manualProblem);
    }

    // ---------------------------------------------------------------- 1) 安装发现

    public IReadOnlyList<VrcvcCandidate> DiscoverCandidates(out string? manualProblem)
    {
        manualProblem = null;
        var list = new List<VrcvcCandidate>();

        try
        {
            // 手动目录先做一次合法性校验，失败也要把原因带回 UI（而不是静默忽略）。
            if (!string.IsNullOrWhiteSpace(ManualInstallDir))
            {
                if (!VrcvcLocator.Validate(ManualInstallDir))
                    manualProblem = $"手动选择的目录下没有 {VrcvcLocator.ExeName}：{ManualInstallDir}";
            }

            var installs = VrcvcLocator.Discover(ManualInstallDir);
            foreach (var install in installs)
            {
                try
                {
                    list.Add(VrcvcCandidate.From(install));
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("manager", $"候选读取失败: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "VrcvcLocator.Discover 失败", ex);
            manualProblem ??= $"安装发现失败：{ex.GetType().Name} - {ex.Message}";
        }

        return list;
    }

    public VrcvcVersionInfo DetectVersion(string? installDir)
    {
        try
        {
            // 进程内运行版本优先（若 Hook 已注入，HookStatus 里有 VRCVC 自己的 Program.Version）。
            var hookVersion = SafeReadHookStatus()?.VrcvcVersion;
            return VrcvcLocator.DetectVersion(installDir, hookVersion);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"版本探测失败: {ex.GetType().Name}");
            return new VrcvcVersionInfo(null, "unknown", false, ex.Message);
        }
    }

    private static CompatReport SafeCompat(VrcvcVersionInfo version)
    {
        try
        {
            return CompatibilityMatrix.Evaluate(version.Version);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"兼容性评估失败: {ex.GetType().Name}");
            return CompatibilityMatrix.Evaluate(null);
        }
    }

    // ---------------------------------------------------------------- 2~5) 插件

    public PluginStatus ReadPluginStatus()
    {
        try
        {
            var state = HookInstallation.Inspect();
            var manifest = HookInstallation.ReadManifest();
            var hashShort = "—";

            if (state.DllPresent && state.HookDllPath != null)
            {
                try
                {
                    var hash = HookInstallation.Sha256File(state.HookDllPath);
                    hashShort = hash.Length > 16 ? hash[..16] + "…" : hash;
                }
                catch (Exception ex)
                {
                    hashShort = $"读取失败({ex.GetType().Name})";
                }
            }

            var version = state.HookVersion;
            if (string.IsNullOrWhiteSpace(version) && state.DllPresent && state.HookDllPath != null)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(state.HookDllPath);
                    version = info.ProductVersion ?? info.FileVersion;
                }
                catch
                {
                    // ignore
                }
            }

            var manifestSha = manifest?.Sha256;
            if (manifestSha is { Length: > 16 }) manifestSha = manifestSha[..16] + "…";

            // disabled.flag 的实际路径与当前内容 —— 让用户看懂“禁用 = 让 Hook 静默，契约模式仍生效”。
            var flagExists = false;
            string? flagContent = null;
            try
            {
                flagExists = File.Exists(BridgePaths.DisabledFlag);
                if (flagExists) flagContent = AtomicFile.TryReadAllText(BridgePaths.DisabledFlag);
            }
            catch (Exception ex)
            {
                flagContent = $"读取失败（{ex.GetType().Name}）";
            }

            if (flagContent is { Length: > 0 })
            {
                var firstLine = flagContent.Replace("\r", string.Empty).Split('\n')
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                flagContent = firstLine?.Trim();
            }

            return new PluginStatus(
                Installed: state.Installed,
                Enabled: state.Enabled,
                EnvRegistered: state.EnvRegistered,
                DllPresent: state.DllPresent,
                DllHashMatches: state.DllHashMatches,
                DllPath: state.HookDllPath ?? HookInstallation.HookDllPath,
                Version: version,
                EnvValue: state.EnvValue,
                HashShort: hashShort,
                HasManifest: manifest != null,
                InstalledUtc: manifest?.InstalledUtc,
                ManifestSha: manifestSha,
                DisabledFlagPath: BridgePaths.DisabledFlag,
                DisabledFlagExists: flagExists,
                DisabledFlagContent: flagContent,
                Problems: state.Problems);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "HookInstallation.Inspect 失败", ex);
            return PluginStatus.Empty with { Problems = [$"读取插件状态失败：{ex.GetType().Name} - {ex.Message}"] };
        }
    }

    /// <summary>找出可安装的 Hook DLL：优先程序目录下的 hook\，其次仓库 dev 构建输出。</summary>
    public IReadOnlyList<string> HookSourceCandidates()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full) && File.Exists(full)) results.Add(full);
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        try
        {
            // 1) 部署形态：<程序目录>\hook\CookieBridge.Hook.dll
            Add(Path.Combine(AppContext.BaseDirectory, "hook", HookInstallation.HookAssemblyName));
            Add(Path.Combine(AppContext.BaseDirectory, HookInstallation.HookAssemblyName));

            // 2) dev 形态：从当前目录逐级向上找 bridge\CookieBridge.Hook\bin\<cfg>\<tfm>\
            //    Hook 现在编到 net10.0-windows（Core 改成 windows 目标后随之变化），保留 net10.0 兼容旧输出。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var level = 0; level < 9 && dir != null; level++)
            {
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    foreach (var tfm in new[] { "net10.0-windows", "net10.0" })
                    {
                        Add(Path.Combine(dir.FullName, "CookieBridge.Hook", "bin", cfg, tfm, HookInstallation.HookAssemblyName));
                        Add(Path.Combine(dir.FullName, "bridge", "CookieBridge.Hook", "bin", cfg, tfm, HookInstallation.HookAssemblyName));
                    }
                }
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"Hook 源查找异常: {ex.GetType().Name}");
        }

        return results;
    }

    public OperationResult InstallHook(bool registerStartupHook = false)
    {
        try
        {
            var sources = HookSourceCandidates();
            if (sources.Count == 0)
                return OperationResult.Fail(
                    "找不到 CookieBridge.Hook.dll。请把 hook\\CookieBridge.Hook.dll 放到管理器程序目录下，" +
                    "或在仓库里先执行：dotnet build bridge/CookieBridge.Hook/CookieBridge.Hook.csproj -c Release");

            var source = sources[0];
            var ok = HookInstallation.Install(source, out var message, registerStartupHook);
            BridgeLog.Info("manager",
                ok
                    ? $"插件已部署（源 {source}，registerStartupHook={registerStartupHook}）。"
                    : $"插件部署失败：{message}");

            // 未勾选注册时，直接把 Core 返回的中文文案展示出来（它已经解释清 Steam 版为什么不需要）。
            return new OperationResult(ok, ok ? $"{message}\n源文件：{source}" : message + WriteFailureHint(null));
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "插件安装异常", ex);
            return OperationResult.Fail($"安装失败：{ex.GetType().Name} - {ex.Message}" + WriteFailureHint(ex));
        }
    }

    /// <summary>
    /// 沙箱/安全软件写入受限时的统一提示。lead 观察到的现象：测试工具派生的子进程会被
    /// CodexSandboxUsers 只读 ACL 拦住，用户正常双击运行不受影响。
    /// </summary>
    public static string WriteFailureHint(Exception? ex) =>
        ex is null or UnauthorizedAccessException
            ? "\n提示：当前进程被安全软件/沙箱限制了写入，请从资源管理器直接运行本程序。"
            : string.Empty;

    public OperationResult UninstallHook()
    {
        try
        {
            var ok = HookInstallation.Uninstall(out var message);
            BridgeLog.Info("manager", ok ? "插件已卸载。" : $"插件卸载失败：{message}");
            return new OperationResult(ok, message);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "插件卸载异常", ex);
            return OperationResult.Fail($"卸载失败：{ex.GetType().Name} - {ex.Message}" + WriteFailureHint(ex));
        }
    }

    /// <summary>启用/禁用插件。禁用只写 disabled.flag，不动机器上任何其它状态。</summary>
    public OperationResult SetHookEnabled(bool enabled)
    {
        try
        {
            HookInstallation.SetEnabled(enabled);
            BridgeLog.Info("manager", enabled ? "插件已启用。" : "插件已禁用。");
            return OperationResult.Ok(enabled
                ? "插件已启用：VRCVC 下次启动时会在进程内接管 Cookie 同步。"
                : "插件已禁用：VRCVC 恢复原生行为（启动时直接读取它自己的 youtube_cookies.txt），" +
                  "环境变量保留，随时可以再启用。");
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "启用/禁用异常", ex);
            return OperationResult.Fail($"操作失败：{ex.GetType().Name} - {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 7) Cookie 状态

    public CookieStatus ReadCookieStatus()
    {
        var targetPath = CookieTargetWriter.TargetPath;

        try
        {
            var doc = CookieVault.ReadDocument();
            var vaultExists = File.Exists(BridgePaths.VaultFile);
            var vaultUpdated = CookieVault.LastUpdatedUtc();

            var target = CookieTargetWriter.ReadState();

            return new CookieStatus(
                VaultExists: vaultExists,
                VaultReadable: doc != null,
                VaultCookieCount: doc?.Metadata.CookieCount ?? 0,
                VaultLogin: doc?.Metadata.LoginCookiePresent ?? false,
                VaultUpdatedUtc: doc?.UpdatedUtc ?? vaultUpdated,
                VaultProtection: doc?.Protection,
                VaultSource: doc?.Metadata.SourceBrowser is { Length: > 0 } b
                    ? (doc.Metadata.ExtensionVersion is { Length: > 0 } v ? $"{b} / 扩展 {v}" : b)
                    : null,
                VaultError: vaultExists && doc == null ? "vault 头无法解析" : null,
                TargetPath: targetPath,
                TargetExists: target.Exists,
                TargetReadable: target.Readable,
                TargetValid: target.Valid,
                TargetCookieCount: target.CookieCount,
                TargetLogin: target.LoginCookiePresent,
                TargetLastWriteUtc: target.LastWriteUtc,
                TargetSize: target.SizeBytes,
                TargetError: target.Error);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "读取 Cookie 状态失败", ex);
            return CookieStatus.Empty with
            {
                TargetPath = targetPath,
                TargetError = $"{ex.GetType().Name} - {ex.Message}",
            };
        }
    }

    /// <summary>解密校验 vault（自检用）：只看能否解开 + 条数，绝不把内容带出来。</summary>
    public (bool Ok, string Detail) VerifyVault()
    {
        try
        {
            var result = CookieVault.Load();
            if (result.Success)
                return (true, $"可解密，{result.Summary.CookieCount} 条，登录 Cookie={result.Summary.LoginCookiePresent}");

            return (false, $"读取失败：{result.Error ?? "unknown"}");
        }
        catch (Exception ex)
        {
            return (false, $"读取异常：{ex.GetType().Name} - {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 8) 插件运行状态

    private static HookStatus? SafeReadHookStatus()
    {
        try
        {
            return HookStatus.Read();
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"HookStatus.Read 失败: {ex.GetType().Name}");
            return null;
        }
    }

    public LiveStatus ReadLiveStatus()
    {
        try
        {
            var status = SafeReadHookStatus();
            var running = VrcvcProcessState.IsRunning();
            int? runningPid = null;
            try { runningPid = VrcvcProcessState.RunningPid(); } catch { runningPid = null; }

            var fileExists = File.Exists(BridgePaths.HookStatusFile);

            if (status == null)
            {
                return new LiveStatus(false, 0, null, null, null, null, null, null, 0, null, false, [],
                    running, runningPid,
                    fileExists ? "hook-status.json 存在但无法解析" : null);
            }

            var layers = new List<LayerEntry>();
            try
            {
                foreach (var kv in status.Layers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    layers.Add(new LayerEntry(kv.Key, kv.Value));
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("manager", $"Layers 读取失败: {ex.GetType().Name}");
            }

            return new LiveStatus(
                Fresh: status.IsFresh,
                Pid: status.Pid,
                VrcvcVersion: status.VrcvcVersion,
                HookVersion: status.HookVersion,
                StartedUtc: status.StartedUtc == default ? null : status.StartedUtc,
                HeartbeatUtc: status.LastHeartbeatUtc == default ? null : status.LastHeartbeatUtc,
                LastSyncUtc: status.LastSyncUtc,
                LastSyncOutcome: status.LastSyncOutcome,
                SyncCount: status.SyncCount,
                LastError: status.LastError,
                VaultWatching: status.VaultWatching,
                Layers: layers,
                ProcessRunning: running,
                ProcessPid: runningPid,
                StatusFileError: null);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "读取插件运行状态失败", ex);
            return LiveStatus.Empty with { StatusFileError = $"{ex.GetType().Name} - {ex.Message}" };
        }
    }

    // ---------------------------------------------------------------- 12) Native Host

    /// <summary>浏览器扩展 ID：优先 &lt;程序目录&gt;\browser-extension\extension-id.txt。</summary>
    public (string? ExtensionId, string? FirefoxId) ReadExtensionIdentity()
    {
        try
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "browser-extension", "extension-id.txt"),
            };

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var level = 0; level < 9 && dir != null; level++)
            {
                candidates.Add(Path.Combine(dir.FullName, "browser-extension", "extension-id.txt"));
                dir = dir.Parent;
            }

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;

                string? extensionId = null;
                string? firefoxId = null;

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;

                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;

                    var key = line[..idx].Trim();
                    var value = line[(idx + 1)..].Trim();
                    if (value.Length == 0) continue;

                    if (key.Equals("extension_id", StringComparison.OrdinalIgnoreCase)) extensionId = value;
                    else if (key.Equals("gecko_id", StringComparison.OrdinalIgnoreCase)) firefoxId = value;
                }

                if (extensionId != null || firefoxId != null)
                    return (extensionId, firefoxId);
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"读取 extension-id.txt 失败: {ex.GetType().Name}");
        }

        return (null, null);
    }

    public NativeStatus ReadNativeStatus()
    {
        try
        {
            var registered = NativeHostRegistration.GetRegisteredTargets()
                .Select(t => t.DisplayName)
                .ToList();

            var known = NativeHostRegistration.Targets.Select(t => t.DisplayName).ToList();

            return new NativeStatus(
                registered,
                NativeHostRegistration.ChromiumManifestPath,
                NativeHostRegistration.FirefoxManifestPath,
                FindNativeHostExe(),
                FindNativeHostExe() != null,
                known);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"读取 Native Host 状态失败: {ex.GetType().Name}");
            return NativeStatus.Empty with { HostExePath = null };
        }
    }

    /// <summary>本地写入程序（CookieBridge.NativeHost.exe）的位置。</summary>
    public string? FindNativeHostExe()
    {
        const string exeName = "CookieBridge.NativeHost.exe";

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "host", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
        };

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var level = 0; level < 9 && dir != null; level++)
            {
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    candidates.Add(Path.Combine(dir.FullName, "CookieBridge.NativeHost", "bin", cfg, "net10.0-windows", "win-x64", "publish", exeName));
                    candidates.Add(Path.Combine(dir.FullName, "CookieBridge.NativeHost", "bin", cfg, "net10.0-windows", "win-x64", exeName));
                    candidates.Add(Path.Combine(dir.FullName, "CookieBridge.NativeHost", "bin", cfg, "net10.0", exeName));
                    candidates.Add(Path.Combine(dir.FullName, "bridge", "CookieBridge.NativeHost", "bin", cfg, "net10.0-windows", "win-x64", "publish", exeName));
                    candidates.Add(Path.Combine(dir.FullName, "bridge", "CookieBridge.NativeHost", "bin", cfg, "net10.0-windows", "win-x64", exeName));
                    candidates.Add(Path.Combine(dir.FullName, "bridge", "CookieBridge.NativeHost", "bin", cfg, "net10.0", exeName));
                }
                candidates.Add(Path.Combine(dir.FullName, "bridge", "CookieBridge.NativeHost", exeName));
                dir = dir.Parent;
            }
        }
        catch
        {
            // ignore
        }

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    public OperationResult RegisterNativeHost(string extensionId, string firefoxId)
    {
        try
        {
            var hostExe = FindNativeHostExe();
            if (hostExe == null)
                return OperationResult.Fail(
                    "找不到 CookieBridge.NativeHost.exe。请先发布本地写入程序（dotnet publish bridge/CookieBridge.NativeHost -c Release），" +
                    "再把可执行文件放到管理器目录下的 host\\ 子目录里。");

            var chromium = SplitIds(extensionId);
            var firefox = SplitIds(firefoxId);

            if (chromium.Count == 0 && firefox.Count == 0)
                return OperationResult.Fail("请先填写浏览器扩展 ID（Chromium 系）或 Firefox 扩展 ID。");

            var ok = NativeHostRegistration.Register(hostExe, chromium, firefox, out var message);
            return new OperationResult(ok, message);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "注册 Native Host 异常", ex);
            return OperationResult.Fail($"注册失败：{ex.GetType().Name} - {ex.Message}");
        }
    }

    public OperationResult UnregisterNativeHost()
    {
        try
        {
            var ok = NativeHostRegistration.Unregister(out var message);
            return new OperationResult(ok, message);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "注销 Native Host 异常", ex);
            return OperationResult.Fail($"注销失败：{ex.GetType().Name} - {ex.Message}");
        }
    }

    private static List<string> SplitIds(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var part in raw.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var id = part.Trim();
            if (id.Length == 0) continue;

            // 允许用户直接粘贴 chrome-extension://<id>/ 形式
            if (id.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = id["chrome-extension://".Length..].TrimEnd('/');
                if (trimmed.Length > 0) list.Add(trimmed);
                continue;
            }

            list.Add(id);
        }

        return list;
    }

    // ---------------------------------------------------------------- 14) Steam

    public SteamStatus ReadSteamStatus(DateTimeOffset? installedUtc)
    {
        try
        {
            var steamExe = SteamExePath();

            Process? process = null;
            try
            {
                process = Process.GetProcessesByName("steam").FirstOrDefault();
                if (process == null)
                    return new SteamStatus(false, null, steamExe, false, installedUtc);

                DateTimeOffset started;
                try
                {
                    started = new DateTimeOffset(process.StartTime);
                }
                catch (Exception ex)
                {
                    // 权限不足时 StartTime 会抛，退化成“无法判定”。
                    BridgeLog.Warn("manager", $"steam.exe StartTime 不可读: {ex.GetType().Name}");
                    return new SteamStatus(true, null, steamExe, false, installedUtc);
                }

                var stale = installedUtc.HasValue && started < installedUtc.Value;
                return new SteamStatus(true, started, steamExe, stale, installedUtc);
            }
            finally
            {
                process?.Dispose();
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("manager", $"Steam 状态读取失败: {ex.GetType().Name}");
            return SteamStatus.Empty with { InstalledUtc = installedUtc };
        }
    }

    public string? SteamExePath()
    {
        try
        {
            var root = VrcvcLocator.ReadSteamPath();
            if (string.IsNullOrWhiteSpace(root)) return null;
            var exe = Path.Combine(root, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 重启 Steam：先让 Steam 自己优雅退出（-shutdown），确认退出后再启动。
    /// 调用方必须已经弹过确认框 —— 这个方法不做二次确认。
    /// </summary>
    public async Task<OperationResult> RestartSteamAsync(CancellationToken token = default)
    {
        try
        {
            var exe = SteamExePath();
            if (exe == null)
                return OperationResult.Fail("找不到 steam.exe（Steam 路径未从注册表读到），请手动重启 Steam。");

            var before = Process.GetProcessesByName("steam");
            try
            {
                if (before.Length > 0)
                {
                    BridgeLog.Info("manager", "发送 steam -shutdown。");
                    using var shutdown = Process.Start(new ProcessStartInfo(exe, "-shutdown")
                    {
                        UseShellExecute = true,
                    });
                }
            }
            finally
            {
                foreach (var p in before) p.Dispose();
            }

            // 最多等 60 秒，让 Steam 自行退出。
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var alive = Process.GetProcessesByName("steam");
                var running = alive.Length > 0;
                foreach (var p in alive) p.Dispose();
                if (!running) break;
                await Task.Delay(500, token).ConfigureAwait(true);
            }

            var still = Process.GetProcessesByName("steam");
            var stillRunning = still.Length > 0;
            foreach (var p in still) p.Dispose();

            if (stillRunning)
                return OperationResult.Fail("Steam 在 60 秒内没有退出，已放弃自动重启。请手动退出 Steam 后重新打开。");

            BridgeLog.Info("manager", "Steam 已退出，重新启动。");
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            });

            return OperationResult.Ok("Steam 已重启。启动完成后 VRCVideoCacher 会继承最新的 DOTNET_STARTUP_HOOKS 环境变量。");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail("已取消重启 Steam。");
        }
        catch (Exception ex)
        {
            BridgeLog.Error("manager", "重启 Steam 失败", ex);
            return OperationResult.Fail($"重启 Steam 失败：{ex.GetType().Name} - {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 13) 自检报告

    /// <summary>
    /// 逐项体检并生成可复制文本。所有结论都来自 Core 的真实读取，任何一项异常都不会中断报告。
    /// </summary>
    public string BuildSelfTestReport(ManagerSnapshot snapshot)
    {
        var sb = new StringBuilder(4096);
        var problems = 0;
        var warnings = 0;

        void Header(string title)
        {
            sb.AppendLine();
            sb.AppendLine($"== {title} ==");
        }

        void Ok(string text)
        {
            sb.AppendLine($"[OK]   {text}");
        }

        void Warn(string text)
        {
            warnings++;
            sb.AppendLine($"[WARN] {text}");
        }

        void Bad(string text)
        {
            problems++;
            sb.AppendLine($"[FAIL] {text}");
        }

        sb.AppendLine("VRCVideoCacher CookieBridge —— 运行自检报告");
        sb.AppendLine($"生成时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"管理器版本: {ManagerVersion}");
        sb.AppendLine($"桥接根目录: {BridgePaths.Root}");

        // ---- 0. 运行模式 ----
        Header("0. 运行模式");
        var inProcess = snapshot.Live.Fresh;
        sb.AppendLine($"当前模式: {(inProcess ? "进程内模式（In-process mode）" : "契约模式（Contract mode）")}");
        sb.AppendLine($"判定依据: HookStatus.IsFresh = {snapshot.Live.Fresh.ToString().ToLowerInvariant()}" +
                      $"（心跳 {FormatTime(snapshot.Live.HeartbeatUtc)}，PID {snapshot.Live.Pid}）");
        if (inProcess)
        {
            Ok("Hook 已注入 VRCVC 进程：vault 变化会被进程内插件即时同步到 Cookie 文件。");
        }
        else
        {
            Ok("契约模式生效：Cookie 由本地写入程序（NativeHost）直接写入 " +
               "%APPDATA%\\VRCVideoCacher\\youtube_cookies.txt —— 这是 VRCVC 唯一消费点，功能完整（自动更新、无需重启、原子写入）。");
            sb.AppendLine("       说明: Steam 版 VRCVideoCacher 为单文件自包含发布，宿主把 System.StartupHookProvider.IsSupported");
            sb.AppendLine("             设为 false 且 TRUSTED_PLATFORM_ASSEMBLIES 为空，因此 CoreCLR 启动钩子无法载入该进程。");
            sb.AppendLine("             这是宿主发布形态限制，不是安装失败；进程内模式仅在框架依赖宿主 / 自编译 fork 上可用。");
        }

        // ---- 1. VRCVC 安装 ----
        Header("1. VRCVideoCacher 安装");
        if (snapshot.Candidates.Count == 0)
        {
            Bad("未发现任何 VRCVideoCacher 安装（Steam 库注册表 + libraryfolders.vdf + Program Files 都查过）。");
        }
        else
        {
            Ok($"发现 {snapshot.Candidates.Count} 个候选安装：");
            foreach (var c in snapshot.Candidates)
                sb.AppendLine($"       - {c.InstallDir}  版本={c.VersionText}（{c.VersionSource}） 来源={c.SourceText}");
        }

        if (snapshot.ManualDirProblem != null) Warn(snapshot.ManualDirProblem);

        if (snapshot.Selected != null)
        {
            Ok($"当前选中: {snapshot.Selected.InstallDir}");
            Ok($"可执行文件存在: {File.Exists(snapshot.Selected.ExePath)}");
        }

        // ---- 2. 版本 / 兼容性 ----
        Header("2. 版本与兼容性");
        sb.AppendLine($"VRCVC 版本 : {snapshot.Version.Version ?? "unknown"}（来源 {snapshot.Version.Source}" +
                      $"{(snapshot.Version.FromRunningProcess ? "，来自运行中进程" : string.Empty)}）");
        sb.AppendLine($"兼容结论   : {snapshot.Compat.LevelLabel} —— {snapshot.Compat.Headline}");
        foreach (var note in snapshot.Compat.Notes) sb.AppendLine($"  · {note}");
        sb.AppendLine($"插件是否允许加载: {(snapshot.Compat.HookAllowed ? "是" : "否")}");

        if (snapshot.Version.Known) Ok($"版本号可识别：{snapshot.Version.Version}");
        else Warn("版本号未知——插件会以“仅 Cookie 文件契约”模式工作，不影响 VRCVC 启动。");

        if (snapshot.Compat.Level == CompatLevel.Verified) Ok("该版本已完整验证。");
        else if (snapshot.Compat.Level is CompatLevel.UntestedNewer or CompatLevel.UntestedOlder)
            Warn($"未验证版本（{snapshot.Compat.LevelLabel}），建议关注 Layers 探测结果。");

        // ---- 3. Hook DLL ----
        Header("3. 插件 DLL");
        sb.AppendLine($"期望路径: {HookInstallation.HookDllPath}");
        if (snapshot.Plugin.DllPresent)
        {
            Ok($"DLL 存在，版本 {snapshot.Plugin.Version ?? "未知"}，SHA256 前缀 {snapshot.Plugin.HashShort}");
            if (snapshot.Plugin.DllHashMatches) Ok("哈希与安装记录一致。");
            else Bad("哈希与 install.json 记录不一致（DLL 可能被替换或安装记录过期），建议点“更新插件”重装。");
        }
        else
        {
            Bad("插件 DLL 未安装。");
            var sources = HookSourceCandidates();
            if (sources.Count > 0) Ok($"可用的安装源: {sources[0]}");
            else sb.AppendLine("       安装源: 未找到 CookieBridge.Hook.dll（需要先构建 Hook 项目）。");
        }

        // ---- 4. 环境变量 ----
        Header("4. DOTNET_STARTUP_HOOKS（用户级）");
        var env = snapshot.Plugin.EnvValue;
        sb.AppendLine($"当前值: {(string.IsNullOrWhiteSpace(env) ? "（未设置）" : env)}");
        sb.AppendLine($"是否包含本插件路径（HookInstallation.Inspect().EnvRegistered）= " +
                      $"{(snapshot.Plugin.EnvRegistered ? "是" : "否")}");
        sb.AppendLine($"期望的插件路径: {HookInstallation.HookDllPath}");

        if (string.IsNullOrWhiteSpace(env))
        {
            Warn("用户级 DOTNET_STARTUP_HOOKS 未设置 —— 对 Steam 版没有影响（单文件宿主不加载启动钩子）；" +
                 "只有自编译 fork / 框架依赖宿主才需要，可在安装时勾选“同时注册 DOTNET_STARTUP_HOOKS”。");
        }
        else
        {
            if (snapshot.Plugin.EnvRegistered) Ok("其中包含本插件 DLL。");
            else Warn("其中不包含本插件 DLL（若你并不打算用进程内模式，这是正常状态）。");

            var entries = env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length > 1)
                Warn($"该变量里共有 {entries.Length} 个条目（其它工具注入的 hook 会被保留，不会被覆盖）。");

            sb.AppendLine("注意: 该变量会让插件被注入到用户启动的所有支持启动钩子的 .NET 程序（插件会自行退出），" +
                          "所以安装时默认不注册。");
        }

        // ---- 5. 禁用标记 ----
        Header("5. 禁用标记");
        if (snapshot.Plugin.Enabled) Ok("disabled.flag 不存在 —— 插件处于启用状态。");
        else Warn($"disabled.flag 存在 —— 插件被禁用，VRCVC 会恢复原生行为（{BridgePaths.DisabledFlag}）。");

        if (snapshot.Plugin.Problems.Count > 0)
        {
            sb.AppendLine("Core 报告的问题：");
            foreach (var p in snapshot.Plugin.Problems) sb.AppendLine($"  · {p}");
        }

        // ---- 6. Vault ----
        Header("6. Cookie Vault（密文）");
        sb.AppendLine($"路径: {BridgePaths.VaultFile}");
        if (!snapshot.Cookie.VaultExists)
        {
            Warn("vault.json 不存在 —— 浏览器扩展还没有推送过 Cookie。");
        }
        else
        {
            var verify = VerifyVault();
            if (verify.Ok)
                Ok($"vault 可解密：{verify.Detail}");
            else
                Bad($"vault 读取失败：{verify.Detail}");

            sb.AppendLine($"最后更新: {FormatTime(snapshot.Cookie.VaultUpdatedUtc)}" +
                          $"（文件时间 {FormatTime(CookieVault.LastUpdatedUtc())}）");
            sb.AppendLine($"Cookie 条数: {snapshot.Cookie.VaultCookieCount}，登录 Cookie: {(snapshot.Cookie.VaultLogin ? "存在" : "缺失")}");
            sb.AppendLine($"保护方式: {snapshot.Cookie.VaultProtection ?? "—"}，来源浏览器: {snapshot.Cookie.VaultSource ?? "—"}");
        }

        // ---- 7. 目标文件 ----
        Header("7. VRCVC 目标 Cookie 文件");
        sb.AppendLine($"路径: {snapshot.Cookie.TargetPath}");
        if (!snapshot.Cookie.TargetExists)
        {
            Warn("目标文件不存在 —— VRCVC 目前没有登录 Cookie（它会在启动时自行创建）。");
        }
        else
        {
            sb.AppendLine($"存在=是，可读={(snapshot.Cookie.TargetReadable ? "是" : "否")}，" +
                          $"VRCVC 判定有效={(snapshot.Cookie.TargetValid ? "是" : "否")}");
            sb.AppendLine($"条数={snapshot.Cookie.TargetCookieCount}，登录 Cookie={(snapshot.Cookie.TargetLogin ? "存在" : "缺失")}，" +
                          $"大小={snapshot.Cookie.TargetSize} 字节，最后写入={FormatTime(snapshot.Cookie.TargetLastWriteUtc)}");

            if (snapshot.Cookie.TargetValid) Ok("目标文件满足 VRCVC 的有效性判定（含 youtube.com + LOGIN_INFO）。");
            else Bad("目标文件不满足 VRCVC 的有效性判定，YouTube 仍需登录。");
            if (snapshot.Cookie.TargetError != null) Bad($"读取错误: {snapshot.Cookie.TargetError}");
        }

        // ---- 7b. 契约模式是否真的在工作 ----
        Header("7b. 契约模式可用性判定");
        sb.AppendLine($"目标 Cookie 文件: 存在={(snapshot.Cookie.TargetExists ? "是" : "否")}，" +
                      $"有效={(snapshot.Cookie.TargetValid ? "是" : "否")}，" +
                      $"条数={snapshot.Cookie.TargetCookieCount}，" +
                      $"最后写入={FormatTime(snapshot.Cookie.TargetLastWriteUtc)}");
        sb.AppendLine($"Vault: 存在={(snapshot.Cookie.VaultExists ? "是" : "否")}，" +
                      $"更新时间={FormatTime(snapshot.Cookie.VaultUpdatedUtc)}，" +
                      $"条数={snapshot.Cookie.VaultCookieCount}，" +
                      $"登录 Cookie={(snapshot.Cookie.VaultLogin ? "有" : "无")}");

        if (snapshot.Cookie.TargetValid)
        {
            Ok("契约模式工作正常：VRCVC 读取 youtube_cookies.txt 时能拿到有效的 YouTube 登录 Cookie。");
        }
        else if (snapshot.Cookie.VaultExists)
        {
            Warn("vault 有数据，但目标文件当前无效/不存在 —— 等本地写入程序下一次推送，" +
                 "或确认浏览器扩展正在运行且已登录 YouTube。");
        }
        else
        {
            Bad("vault 与目标文件都不可用 —— 需要先在浏览器扩展里登录 YouTube 并推送一次 Cookie。");
        }

        sb.AppendLine($"写入策略: 若检测到 yt-dlp 正在运行，写入会延后，以避免丢失 YouTube 令牌轮换" +
                      "（与上游 YtdlpCookieJar 同款顾虑）；写入为原子替换 + 跨进程互斥量。");

        // ---- 8. Native Host ----
        Header("8. Native Messaging Host");
        if (snapshot.Native.RegisteredBrowsers.Count > 0)
            Ok($"已注册: {string.Join("、", snapshot.Native.RegisteredBrowsers)}");
        else
            Warn("尚未注册任何浏览器 —— 浏览器扩展无法把 Cookie 交给本地写入程序。");

        sb.AppendLine($"写入程序: {snapshot.Native.HostExePath ?? "未找到 CookieBridge.NativeHost.exe"}");
        sb.AppendLine($"Chromium 清单: {snapshot.Native.ChromiumManifestPath}（存在={File.Exists(snapshot.Native.ChromiumManifestPath)}）");
        sb.AppendLine($"Firefox  清单: {snapshot.Native.FirefoxManifestPath}（存在={File.Exists(snapshot.Native.FirefoxManifestPath)}）");
        if (snapshot.Native.RegisteredBrowsers.Count > 0 && !snapshot.Native.HostExeFound)
            Bad("注册项指向的本地写入程序不存在（可能被移动/删除），请重新注册。");

        // ---- 9. 插件是否在进程内 ----
        Header("9. 插件运行状态（是否已注入 VRCVC）");
        sb.AppendLine($"HookStatus.IsFresh = {snapshot.Live.Fresh.ToString().ToLowerInvariant()} " +
                      $"（判定：心跳 ≤ 90 秒且 PID 仍然存活）");
        sb.AppendLine($"hook-status.json 心跳 = {FormatTime(snapshot.Live.HeartbeatUtc)}，" +
                      $"PID = {snapshot.Live.Pid}，VRCVC 进程在运行 = {(snapshot.Live.ProcessRunning ? "是" : "否")}");

        if (snapshot.Live.Fresh)
        {
            Ok($"插件正在 VRCVC 进程内运行：PID {snapshot.Live.Pid}，VRCVC 版本 {snapshot.Live.VrcvcVersion ?? "unknown"}");
            sb.AppendLine($"心跳: {FormatTime(snapshot.Live.HeartbeatUtc)}，最后同步: {FormatTime(snapshot.Live.LastSyncUtc)}" +
                          $"（{snapshot.Live.LastSyncOutcome ?? "—"}），累计同步 {snapshot.Live.SyncCount} 次");
        }
        else if (snapshot.Live.ProcessRunning)
        {
            Warn($"VRCVC 正在运行（PID {snapshot.Live.ProcessPid}）但插件没有心跳 —— 说明该进程启动时还没有插件环境变量，" +
                 "退出 VRCVC 后重新启动即可。");
        }
        else
        {
            Warn("VRCVC 未运行 —— 独立模式：Cookie 只在启动时被读取，属于正常状态。");
        }

        if (snapshot.Live.StatusFileError != null) Warn(snapshot.Live.StatusFileError);
        if (snapshot.Live.LastError != null) Warn($"插件报告的最后错误: {snapshot.Live.LastError}");

        if (snapshot.Live.Layers.Count > 0)
        {
            sb.AppendLine("Layers 探测:");
            foreach (var layer in snapshot.Live.Layers)
                sb.AppendLine($"       {layer.Layer,-20} {layer.Value}");
        }
        else
        {
            sb.AppendLine($"Layers 探测: 未运行（{(snapshot.Live.Fresh ? "插件未上报" : "当前为契约模式，没有进程内插件")}）");
        }

        // ---- 10. Steam 环境变量时效 ----
        Header("10. Steam 环境变量时效");
        sb.AppendLine($"install.json 安装时间: {FormatTime(snapshot.Steam.InstalledUtc)}");
        sb.AppendLine($"steam.exe 启动时间  : {FormatTime(snapshot.Steam.SteamStartedUtc)}（运行中={snapshot.Steam.SteamRunning}）");
        if (!snapshot.Steam.SteamRunning)
            Ok("Steam 当前未运行 —— 下次启动 Steam 时会读到最新环境变量。");
        else if (snapshot.Steam.SteamStartedUtc == null)
            Warn("无法读取 steam.exe 启动时间（权限或进程受限），无法判定环境变量是否过期。");
        else if (snapshot.Steam.EnvMayBeStale)
            Warn("Steam 早于插件安装时间启动 —— 它（以及它启动的 VRCVC）可能仍持有旧环境变量。需要重启 Steam 一次。");
        else
            Ok("Steam 晚于插件安装时间启动 —— 环境变量应当已经生效。");

        // ---- 11. 环境一览 ----
        Header("11. 路径一览");
        sb.AppendLine($"桥接根目录        : {BridgePaths.Root}");
        sb.AppendLine($"日志文件          : {BridgePaths.LogFile}");
        sb.AppendLine($"状态目录          : {BridgePaths.StateDir}");
        sb.AppendLine($"安装清单          : {BridgePaths.InstallManifest}");
        sb.AppendLine($"备份目录          : {BackupService.BackupRoot}");
        sb.AppendLine($"VRCVC 数据目录    : {BridgePaths.VrcvcDataDir}");
        sb.AppendLine();
        sb.AppendLine("写入受限提示: 若本报告里出现 UnauthorizedAccessException（例如桥接根目录不可建），" +
                      "说明当前进程被安全软件/沙箱限制了写入，请从资源管理器直接运行本程序。");

        sb.AppendLine();
        sb.AppendLine($"结论: {(problems == 0 ? "未发现致命问题" : $"{problems} 项需要处理")}，{warnings} 项提醒。");
        return sb.ToString();
    }

    public static string ManagerVersion =>
        typeof(ManagerService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    internal static string FormatTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    // ---------------------------------------------------------------- 小工具

    /// <summary>打开资源管理器到指定目录（不存在则尽量创建/上溯到存在的父目录）。</summary>
    public OperationResult OpenDirectory(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return OperationResult.Fail("路径为空。");

            var target = path;
            if (!Directory.Exists(target))
            {
                var parent = Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar));
                target = parent ?? target;
            }

            if (!Directory.Exists(target))
                return OperationResult.Fail($"目录不存在：{path}");

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
            return OperationResult.Ok($"已打开：{target}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"打开目录失败：{ex.GetType().Name} - {ex.Message}");
        }
    }
}
