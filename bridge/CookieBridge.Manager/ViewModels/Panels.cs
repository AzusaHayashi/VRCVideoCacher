using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CookieBridge.Manager.Services;
using CookieBridge.Core;

namespace CookieBridge.Manager.ViewModels;

/// <summary>
/// 三个指示灯共用的颜色（已 Freeze，可安全跨线程复用）。
/// </summary>
public static class Lights
{
    public static readonly Brush Green = Freeze(0x1E, 0x9E, 0x4C);
    public static readonly Brush Yellow = Freeze(0xD8, 0x92, 0x00);
    public static readonly Brush Red = Freeze(0xC6, 0x2D, 0x28);
    public static readonly Brush Gray = Freeze(0x9A, 0xA0, 0xAC);
    public static readonly Brush Blue = Freeze(0x2C, 0x6F, 0xB4);
    public static readonly Brush BlueText = Freeze(0x1F, 0x5C, 0x9E);
    public static readonly Brush GreenText = Freeze(0x1B, 0x6E, 0x3A);
    public static readonly Brush YellowText = Freeze(0x8A, 0x5C, 0x00);
    public static readonly Brush RedText = Freeze(0x9E, 0x22, 0x1D);
    public static readonly Brush GrayText = Freeze(0x5F, 0x66, 0x74);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static string Time(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public static string YesNo(bool value, string yes = "是", string no = "否") => value ? yes : no;

    public static string Truncate(string? text, int max = 260)
    {
        if (string.IsNullOrEmpty(text)) return "—";
        var single = text.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single[..max] + $" …（共 {single.Length} 字符，已截断）";
    }
}

/// <summary>顶部“状态总览”卡片：插件 / Cookie / VRCVC 三个指示灯。</summary>
public sealed class OverviewPanel : ObservableObject
{
    private Brush _hookLight = Lights.Gray;
    private Brush _hookTextBrush = Lights.GrayText;
    private string _hookState = "未知";
    private string _hookDetail = string.Empty;

    private Brush _cookieLight = Lights.Gray;
    private Brush _cookieTextBrush = Lights.GrayText;
    private string _cookieState = "未知";
    private string _cookieDetail = string.Empty;

    private Brush _vrcvcLight = Lights.Gray;
    private Brush _vrcvcTextBrush = Lights.GrayText;
    private string _vrcvcState = "未知";
    private string _vrcvcDetail = string.Empty;

    private string _headline = "尚未读取状态";

    public Brush HookLight { get => _hookLight; set => Set(ref _hookLight, value); }
    public Brush HookTextBrush { get => _hookTextBrush; set => Set(ref _hookTextBrush, value); }
    public string HookState { get => _hookState; set => Set(ref _hookState, value); }
    public string HookDetail { get => _hookDetail; set => Set(ref _hookDetail, value); }

    public Brush CookieLight { get => _cookieLight; set => Set(ref _cookieLight, value); }
    public Brush CookieTextBrush { get => _cookieTextBrush; set => Set(ref _cookieTextBrush, value); }
    public string CookieState { get => _cookieState; set => Set(ref _cookieState, value); }
    public string CookieDetail { get => _cookieDetail; set => Set(ref _cookieDetail, value); }

    public Brush VrcvcLight { get => _vrcvcLight; set => Set(ref _vrcvcLight, value); }
    public Brush VrcvcTextBrush { get => _vrcvcTextBrush; set => Set(ref _vrcvcTextBrush, value); }
    public string VrcvcState { get => _vrcvcState; set => Set(ref _vrcvcState, value); }
    public string VrcvcDetail { get => _vrcvcDetail; set => Set(ref _vrcvcDetail, value); }

    public string Headline { get => _headline; set => Set(ref _headline, value); }

    public void Apply(ManagerSnapshot snapshot)
    {
        // 颜色语义（lead 定）：红色只留给“真的坏了”的情况。
        // 特别是 Steam 版单文件宿主无法进程内注入 —— 那是预期状态，用中性色 + 说明文字，绝不报红。

        // --- 插件灯 ---
        if (!snapshot.Plugin.DllPresent)
        {
            HookLight = Lights.Gray;
            HookTextBrush = Lights.GrayText;
            HookState = "未部署";
            HookDetail = "Hook DLL 不在桥接目录里；Steam 版可以只用契约模式";
        }
        else if (!snapshot.Plugin.Enabled)
        {
            HookLight = Lights.Yellow;
            HookTextBrush = Lights.YellowText;
            HookState = "已禁用";
            HookDetail = "Hook 完全静默（disabled.flag）；契约模式仍由 NativeHost 生效";
        }
        else if (snapshot.Live.Fresh)
        {
            HookLight = Lights.Green;
            HookTextBrush = Lights.GreenText;
            HookState = "进程内模式 · 已注入";
            HookDetail = $"VRCVC 进程内 PID {snapshot.Live.Pid}";
        }
        else
        {
            HookLight = Lights.Blue;
            HookTextBrush = Lights.BlueText;
            HookState = "契约模式运行中";
            HookDetail = "进程内注入不适用于单文件发布（Steam 版预期状态），Cookie 仍走 yt-dlp 文件契约";
        }

        if (!snapshot.Plugin.DllHashMatches && snapshot.Plugin.DllPresent)
        {
            HookLight = Lights.Yellow;
            HookTextBrush = Lights.YellowText;
            HookDetail += "；DLL 哈希与安装记录不一致";
        }

        // --- Cookie 灯 ---（红色只留给“Vault 不可读 / 目标文件读写出错”）
        if (snapshot.Cookie.TargetValid)
        {
            CookieLight = Lights.Green;
            CookieTextBrush = Lights.GreenText;
            CookieState = "就绪";
            CookieDetail = $"{snapshot.Cookie.TargetCookieCount} 条 · 登录 Cookie 存在";
        }
        else if ((snapshot.Cookie.VaultExists && !snapshot.Cookie.VaultReadable) ||
                 snapshot.Cookie.TargetError != null)
        {
            CookieLight = Lights.Red;
            CookieTextBrush = Lights.RedText;
            CookieState = "读取失败";
            CookieDetail = snapshot.Cookie.VaultExists && !snapshot.Cookie.VaultReadable
                ? "vault 头无法解析"
                : $"目标文件读取错误：{snapshot.Cookie.TargetError}";
        }
        else if (!snapshot.Cookie.VaultExists && !snapshot.Cookie.TargetExists)
        {
            CookieLight = Lights.Yellow;
            CookieTextBrush = Lights.YellowText;
            CookieState = "无 Cookie";
            CookieDetail = "vault 与目标文件都不存在（先在浏览器扩展里登录 YouTube 推送一次）";
        }
        else
        {
            CookieLight = Lights.Yellow;
            CookieTextBrush = Lights.YellowText;
            CookieState = "需登录";
            CookieDetail = snapshot.Cookie.TargetExists
                ? "目标文件存在但缺少 youtube.com/LOGIN_INFO"
                : "vault 有数据但目标文件尚未生成";
        }

        // --- VRCVC 灯 ---
        if (snapshot.Selected == null)
        {
            VrcvcLight = Lights.Yellow;
            VrcvcTextBrush = Lights.YellowText;
            VrcvcState = "未找到";
            VrcvcDetail = "未发现 Steam 版 VRCVideoCacher，可用“选择目录…”手动指定";
        }
        else if (snapshot.Compat.Level == CompatLevel.Verified)
        {
            VrcvcLight = Lights.Green;
            VrcvcTextBrush = Lights.GreenText;
            VrcvcState = snapshot.Version.Version ?? "未知版本";
            VrcvcDetail = snapshot.Compat.LevelLabel;
        }
        else if (snapshot.Version.Known)
        {
            VrcvcLight = Lights.Yellow;
            VrcvcTextBrush = Lights.YellowText;
            VrcvcState = snapshot.Version.Version!;
            VrcvcDetail = snapshot.Compat.LevelLabel;
        }
        else
        {
            VrcvcLight = Lights.Yellow;
            VrcvcTextBrush = Lights.YellowText;
            VrcvcState = "版本未知";
            VrcvcDetail = "仅契约模式运行";
        }

        Headline = $"{DateTime.Now:HH:mm:ss} 已刷新 · 候选安装 {snapshot.Candidates.Count} 个";
    }
}

/// <summary>VRCVideoCacher 安装 / 版本 / 兼容性面板。</summary>
public sealed class VrcvcPanel : ObservableObject
{
    private string _installDir = "—";
    private string _exePath = "—";
    private string _source = "—";
    private string _version = "—";
    private string _versionSource = "—";
    private string _compatLabel = "—";
    private string _compatHeadline = "—";
    private string _compatNotes = "—";
    private string _candidateCount = "0";
    private VrcvcCandidate? _selected;
    private string _manualDirProblem = string.Empty;
    private Visibility _manualProblemVisibility = Visibility.Collapsed;

    public ObservableCollection<VrcvcCandidate> Candidates { get; } = [];

    public VrcvcCandidate? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string InstallDir { get => _installDir; set => Set(ref _installDir, value); }
    public string ExePath { get => _exePath; set => Set(ref _exePath, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public string Version { get => _version; set => Set(ref _version, value); }
    public string VersionSource { get => _versionSource; set => Set(ref _versionSource, value); }
    public string CompatLabel { get => _compatLabel; set => Set(ref _compatLabel, value); }
    public string CompatHeadline { get => _compatHeadline; set => Set(ref _compatHeadline, value); }
    public string CompatNotes { get => _compatNotes; set => Set(ref _compatNotes, value); }
    public string CandidateCount { get => _candidateCount; set => Set(ref _candidateCount, value); }
    public string ManualDirProblem { get => _manualDirProblem; set => Set(ref _manualDirProblem, value); }

    public Visibility ManualProblemVisibility
    {
        get => _manualProblemVisibility;
        set => Set(ref _manualProblemVisibility, value);
    }

    public void Apply(ManagerSnapshot snapshot)
    {
        CandidateCount = snapshot.Candidates.Count.ToString();

        // 只在集合内容真的变了时重建，避免打断用户的选择。
        var sameSet = Candidates.Count == snapshot.Candidates.Count &&
                      Candidates.Zip(snapshot.Candidates).All(p =>
                          string.Equals(p.First.InstallDir, p.Second.InstallDir, StringComparison.OrdinalIgnoreCase));

        if (!sameSet)
        {
            Candidates.Clear();
            foreach (var c in snapshot.Candidates) Candidates.Add(c);
        }

        Selected = Candidates.FirstOrDefault(c =>
                       snapshot.Selected != null &&
                       string.Equals(c.InstallDir, snapshot.Selected.InstallDir, StringComparison.OrdinalIgnoreCase))
                   ?? Candidates.FirstOrDefault();

        InstallDir = snapshot.Selected?.InstallDir ?? "未发现 Steam 版 VRCVideoCacher";
        ExePath = snapshot.Selected?.ExePath ?? "—";
        Source = snapshot.Selected?.SourceText ?? "—";

        Version = snapshot.Version.Version ?? "unknown";
        VersionSource = snapshot.Version.Source +
                        (snapshot.Version.FromRunningProcess ? "（运行中进程）" : string.Empty);
        CompatLabel = snapshot.Compat.LevelLabel;
        CompatHeadline = snapshot.Compat.Headline;
        CompatNotes = snapshot.Compat.Notes.Count == 0
            ? "—"
            : string.Join("\n", snapshot.Compat.Notes.Select(n => "· " + n));

        ManualDirProblem = snapshot.ManualDirProblem ?? string.Empty;
        ManualProblemVisibility = string.IsNullOrEmpty(ManualDirProblem) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>插件安装态面板（DLL / 版本 / 哈希 / 环境变量）。</summary>
public sealed class PluginPanel : ObservableObject
{
    private string _installState = "—";
    private string _version = "—";
    private string _dllPath = "—";
    private string _hash = "—";
    private string _hashState = "—";
    private string _envValue = "—";
    private string _envState = "—";
    private string _enabledState = "—";
    private string _installedUtc = "—";
    private string _problems = "—";
    private string _toggleButtonText = "禁用插件";
    private string _disabledFlagPath = "—";
    private string _disabledFlagState = "—";
    private Brush _light = Lights.Gray;

    /// <summary>可折叠的技术说明（Steam 单文件宿主为什么不支持启动钩子）。</summary>
    public string TechNote { get; } =
        "Steam 版 VRCVideoCacher 是单文件自包含发布：宿主把 System.StartupHookProvider.IsSupported 设为 false，" +
        "且 TRUSTED_PLATFORM_ASSEMBLIES 为空（COREHOST_TRACE 实证）。因此 DOTNET_STARTUP_HOOKS 托管启动钩子" +
        "无法载入真实 Steam 版进程 —— 这是宿主发布形态的限制，不是安装没成功。";

    public string TechNote2 { get; } =
        "「安装插件」的真实语义 = 部署 CookieBridge.Hook.dll 到桥接目录 + 把它的绝对路径注册进用户级 " +
        "DOTNET_STARTUP_HOOKS（供框架依赖宿主 / 自编译 fork 使用），并不等于 Steam 版就能进程内注入。" +
        "「禁用」= 写 disabled.flag 让 Hook 完全静默；此时 Cookie 仍由本地写入程序（NativeHost）通过 VRCVC " +
        "自己的 youtube_cookies.txt 契约生效，功能不受影响。";

    public string InstallSemantics { get; } =
        "注意：安装/更新只影响“能不能进程内注入”，不影响 Cookie 是否生效。Steam 版走的是契约模式" +
        "（NativeHost 直接写 youtube_cookies.txt）。";

    public string InstallState { get => _installState; set => Set(ref _installState, value); }
    public string Version { get => _version; set => Set(ref _version, value); }
    public string DllPath { get => _dllPath; set => Set(ref _dllPath, value); }
    public string Hash { get => _hash; set => Set(ref _hash, value); }
    public string HashState { get => _hashState; set => Set(ref _hashState, value); }
    public string EnvValue { get => _envValue; set => Set(ref _envValue, value); }
    public string EnvState { get => _envState; set => Set(ref _envState, value); }
    public string EnabledState { get => _enabledState; set => Set(ref _enabledState, value); }
    public string InstalledUtc { get => _installedUtc; set => Set(ref _installedUtc, value); }
    public string Problems { get => _problems; set => Set(ref _problems, value); }
    public string ToggleButtonText { get => _toggleButtonText; set => Set(ref _toggleButtonText, value); }
    public string DisabledFlagPath { get => _disabledFlagPath; set => Set(ref _disabledFlagPath, value); }
    public string DisabledFlagState { get => _disabledFlagState; set => Set(ref _disabledFlagState, value); }
    public Brush Light { get => _light; set => Set(ref _light, value); }

    public void Apply(ManagerSnapshot snapshot)
    {
        var plugin = snapshot.Plugin;

        InstallState = plugin.Installed
            ? "已安装"
            : plugin.DllPresent ? "DLL 在，但缺少安装记录（install.json）" : "未安装";

        Version = plugin.Version ?? "—";
        DllPath = plugin.DllPath ?? CookieBridge.Core.HookInstallation.HookDllPath;
        Hash = plugin.DllPresent ? plugin.HashShort : "—（DLL 不存在）";

        HashState = !plugin.DllPresent
            ? "—"
            : plugin.DllHashMatches
                ? $"校验通过（install.json 记录 {plugin.ManifestSha ?? "无"}）"
                : $"不一致（install.json 记录 {plugin.ManifestSha ?? "无"}）";

        EnvValue = string.IsNullOrWhiteSpace(plugin.EnvValue)
            ? "（未设置）"
            : Lights.Truncate(plugin.EnvValue, 300);

        EnvState = plugin.EnvRegistered
            ? "已包含本插件"
            : string.IsNullOrWhiteSpace(plugin.EnvValue) ? "未设置" : "不包含本插件";

        EnabledState = plugin.Enabled ? "启用中" : "已禁用（Hook 静默，契约模式仍生效）";
        InstalledUtc = Lights.Time(plugin.InstalledUtc);
        Problems = plugin.Problems.Count == 0 ? "无" : string.Join("\n", plugin.Problems.Select(p => "· " + p));
        ToggleButtonText = plugin.Enabled ? "禁用插件" : "启用插件";

        DisabledFlagPath = plugin.DisabledFlagPath;
        DisabledFlagState = plugin.DisabledFlagExists
            ? $"存在（{plugin.DisabledFlagContent ?? "内容为空"}）→ Hook 完全静默"
            : "不存在（插件启用中）";

        // 颜色语义（lead 定）：只有真正坏掉才用红色。Steam 版没有进程内注入是预期状态。
        Light = !plugin.DllPresent
            ? Lights.Gray
            : plugin.Enabled ? Lights.Green : Lights.Yellow;
    }
}

/// <summary>Cookie 面板（vault + 目标文件）。</summary>
public sealed class CookiePanel : ObservableObject
{
    private string _vaultState = "—";
    private string _vaultUpdated = "—";
    private string _vaultCount = "—";
    private string _vaultLogin = "—";
    private string _vaultProtection = "—";
    private string _vaultSource = "—";

    private string _targetPath = "—";
    private string _targetExists = "—";
    private string _targetReadable = "—";
    private string _targetValid = "—";
    private string _targetCount = "—";
    private string _targetLogin = "—";
    private string _targetLastWrite = "—";
    private string _targetSize = "—";
    private Brush _light = Lights.Gray;

    /// <summary>写入策略说明（lead 要求）：yt-dlp 运行中会延后写入，避免丢失令牌轮换。</summary>
    public string WritePolicyNote { get; } =
        "写入策略：若检测到 yt-dlp 正在运行，写入会延后，以避免丢失 YouTube 令牌轮换" +
        "（上游 YtdlpCookieJar 同款顾虑）。所有写入都是原子替换 + 跨进程互斥量，不会留下半截文件。";

    public string VaultState { get => _vaultState; set => Set(ref _vaultState, value); }
    public string VaultUpdated { get => _vaultUpdated; set => Set(ref _vaultUpdated, value); }
    public string VaultCount { get => _vaultCount; set => Set(ref _vaultCount, value); }
    public string VaultLogin { get => _vaultLogin; set => Set(ref _vaultLogin, value); }
    public string VaultProtection { get => _vaultProtection; set => Set(ref _vaultProtection, value); }
    public string VaultSource { get => _vaultSource; set => Set(ref _vaultSource, value); }

    public string TargetPath { get => _targetPath; set => Set(ref _targetPath, value); }
    public string TargetExists { get => _targetExists; set => Set(ref _targetExists, value); }
    public string TargetReadable { get => _targetReadable; set => Set(ref _targetReadable, value); }
    public string TargetValid { get => _targetValid; set => Set(ref _targetValid, value); }
    public string TargetCount { get => _targetCount; set => Set(ref _targetCount, value); }
    public string TargetLogin { get => _targetLogin; set => Set(ref _targetLogin, value); }
    public string TargetLastWrite { get => _targetLastWrite; set => Set(ref _targetLastWrite, value); }
    public string TargetSize { get => _targetSize; set => Set(ref _targetSize, value); }
    public Brush Light { get => _light; set => Set(ref _light, value); }

    public void Apply(ManagerSnapshot snapshot)
    {
        var cookie = snapshot.Cookie;

        VaultState = !cookie.VaultExists
            ? "不存在（浏览器扩展还没有推送过 Cookie）"
            : cookie.VaultReadable ? "存在，头部可读" : "存在但无法解析";

        VaultUpdated = Lights.Time(cookie.VaultUpdatedUtc);
        VaultCount = cookie.VaultCookieCount.ToString();
        VaultLogin = Lights.YesNo(cookie.VaultLogin, "存在", "缺失");
        VaultProtection = cookie.VaultProtection ?? "—";
        VaultSource = cookie.VaultSource ?? "—";

        TargetPath = cookie.TargetPath;
        TargetExists = Lights.YesNo(cookie.TargetExists, "存在", "不存在");
        TargetReadable = Lights.YesNo(cookie.TargetReadable, "可读", "不可读");
        TargetValid = Lights.YesNo(cookie.TargetValid, "有效", "无效");
        TargetCount = cookie.TargetCookieCount.ToString();
        TargetLogin = Lights.YesNo(cookie.TargetLogin, "存在", "缺失");
        TargetLastWrite = Lights.Time(cookie.TargetLastWriteUtc);
        TargetSize = cookie.TargetSize == 0 ? "0 字节" : $"{cookie.TargetSize} 字节";

        Light = cookie.TargetValid ? Lights.Green
            : cookie.VaultExists || cookie.TargetExists ? Lights.Yellow
            : Lights.Red;
    }
}

/// <summary>插件运行状态面板（心跳 + Layers 探测表）。</summary>
public sealed class LivePanel : ObservableObject
{
    private string _runningState = "—";
    private string _pid = "—";
    private string _vrcvcVersion = "—";
    private string _hookVersion = "—";
    private string _started = "—";
    private string _heartbeat = "—";
    private string _lastSync = "—";
    private string _syncResult = "—";
    private string _syncCount = "—";
    private string _vaultWatching = "—";
    private string _lastError = "—";
    private string _layersEmpty = "尚未收到插件的 Layers 探测结果";
    private Brush _light = Lights.Gray;

    public ObservableCollection<LayerEntry> Layers { get; } = [];

    public string RunningState { get => _runningState; set => Set(ref _runningState, value); }
    public string Pid { get => _pid; set => Set(ref _pid, value); }
    public string VrcvcVersion { get => _vrcvcVersion; set => Set(ref _vrcvcVersion, value); }
    public string HookVersion { get => _hookVersion; set => Set(ref _hookVersion, value); }
    public string Started { get => _started; set => Set(ref _started, value); }
    public string Heartbeat { get => _heartbeat; set => Set(ref _heartbeat, value); }
    public string LastSync { get => _lastSync; set => Set(ref _lastSync, value); }
    public string SyncResult { get => _syncResult; set => Set(ref _syncResult, value); }
    public string SyncCount { get => _syncCount; set => Set(ref _syncCount, value); }
    public string VaultWatching { get => _vaultWatching; set => Set(ref _vaultWatching, value); }
    public string LastError { get => _lastError; set => Set(ref _lastError, value); }
    public string LayersEmpty { get => _layersEmpty; set => Set(ref _layersEmpty, value); }
    public Brush Light { get => _light; set => Set(ref _light, value); }

    public void Apply(ManagerSnapshot snapshot)
    {
        var live = snapshot.Live;

        RunningState = live.Fresh
            ? "在用（VRCVC 进程内，心跳正常）"
            : live.ProcessRunning
                ? "未在运行（VRCVC 正在运行但插件未注入）"
                : "未在运行（VRCVC 未启动）";

        Pid = live.Fresh || live.Pid > 0 ? live.Pid.ToString() : "—";
        VrcvcVersion = live.VrcvcVersion ?? "—";
        HookVersion = live.HookVersion ?? "—";
        Started = Lights.Time(live.StartedUtc);
        Heartbeat = Lights.Time(live.HeartbeatUtc);
        LastSync = Lights.Time(live.LastSyncUtc);
        SyncResult = live.LastSyncOutcome ?? "—";
        SyncCount = live.SyncCount.ToString();
        VaultWatching = live.VaultWatching ? "是（监听 vault 变化，无需重启 VRCVC）" : "否";
        LastError = string.IsNullOrWhiteSpace(live.LastError) ? "无" : live.LastError;

        if (live.StatusFileError != null) LastError = live.StatusFileError;

        Layers.Clear();
        foreach (var layer in live.Layers) Layers.Add(layer);
        LayersEmpty = Layers.Count == 0
            ? (live.Fresh ? "插件未上报 Layers" : "尚未收到插件的 Layers 探测结果")
            : string.Empty;

        Light = live.Fresh ? Lights.Green : live.ProcessRunning ? Lights.Yellow : Lights.Gray;
    }
}

/// <summary>浏览器 Native Messaging 面板。</summary>
public sealed class NativePanel : ObservableObject
{
    private string _extensionId = string.Empty;
    private string _firefoxId = string.Empty;
    private string _registeredBrowsers = "—";
    private string _chromiumManifest = "—";
    private string _firefoxManifest = "—";
    private string _hostExe = "—";
    private string _registryState = "—";
    private Brush _light = Lights.Gray;

    public string ExtensionId { get => _extensionId; set => Set(ref _extensionId, value); }
    public string FirefoxId { get => _firefoxId; set => Set(ref _firefoxId, value); }
    public string RegisteredBrowsers { get => _registeredBrowsers; set => Set(ref _registeredBrowsers, value); }
    public string ChromiumManifest { get => _chromiumManifest; set => Set(ref _chromiumManifest, value); }
    public string FirefoxManifest { get => _firefoxManifest; set => Set(ref _firefoxManifest, value); }
    public string HostExe { get => _hostExe; set => Set(ref _hostExe, value); }
    public string RegistryState { get => _registryState; set => Set(ref _registryState, value); }
    public Brush Light { get => _light; set => Set(ref _light, value); }

    public void Apply(ManagerSnapshot snapshot, string? extensionIdFromFile, string? firefoxIdFromFile)
    {
        // 用户手动改过就不覆盖；只有为空时才用文件里的默认值。
        if (string.IsNullOrWhiteSpace(ExtensionId) && !string.IsNullOrWhiteSpace(extensionIdFromFile))
            ExtensionId = extensionIdFromFile;

        if (string.IsNullOrWhiteSpace(FirefoxId) && !string.IsNullOrWhiteSpace(firefoxIdFromFile))
            FirefoxId = firefoxIdFromFile;

        var native = snapshot.Native;

        RegisteredBrowsers = native.RegisteredBrowsers.Count == 0
            ? "未注册任何浏览器"
            : string.Join("、", native.RegisteredBrowsers);

        RegistryState = native.RegisteredBrowsers.Count == 0
            ? "未注册"
            : $"已注册 {native.RegisteredBrowsers.Count} / {native.KnownBrowsers.Count} 个浏览器";

        ChromiumManifest = native.ChromiumManifestPath;
        FirefoxManifest = native.FirefoxManifestPath;
        HostExe = native.HostExePath ?? "未找到 CookieBridge.NativeHost.exe（请先发布该程序）";

        Light = native.RegisteredBrowsers.Count == 0
            ? Lights.Yellow
            : native.HostExeFound ? Lights.Green : Lights.Red;
    }
}

/// <summary>备份 / 恢复面板。</summary>
public sealed class BackupPanel : ObservableObject
{
    private BackupEntry? _selected;
    private string _summary = "尚未扫描备份目录";
    private string _explanation = BackupService.Explanation;

    public ObservableCollection<BackupEntry> Items { get; } = [];

    public BackupEntry? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Explanation { get => _explanation; set => Set(ref _explanation, value); }

    public void Apply(IReadOnlyList<BackupEntry> entries)
    {
        var keep = Selected?.Directory;

        Items.Clear();
        foreach (var entry in entries) Items.Add(entry);

        Selected = Items.FirstOrDefault(e =>
                       keep != null && string.Equals(e.Directory, keep, StringComparison.OrdinalIgnoreCase))
                   ?? Items.FirstOrDefault();

        Summary = Items.Count == 0
            ? $"暂无备份（目录：{BackupService.BackupRoot}）"
            : $"共 {Items.Count} 份备份，目录：{BackupService.BackupRoot}";
    }
}
