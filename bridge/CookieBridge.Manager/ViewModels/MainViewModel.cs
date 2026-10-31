using System.Windows;
using System.Windows.Media;
using CookieBridge.Core;
using CookieBridge.Manager.Services;

namespace CookieBridge.Manager.ViewModels;

/// <summary>
/// 主 ViewModel：把 <see cref="ManagerService"/> 的快照映射到各个面板，并把用户动作用命令接起来。
/// 所有命令体内部都 try/catch，失败只写状态栏，UI 不会因为 Core 抛异常而崩溃。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ManagerService _service = new();
    private readonly BackupService _backupService = new();
    private readonly IDialogService _dialogs;

    private ManagerSnapshot? _snapshot;
    private bool _refreshing;
    private bool _applying;

    private string _statusText = "就绪";
    private Brush _statusBrush = Lights.GrayText;
    private string _selfTestReport = "点击“运行自检”生成可复制的体检报告。";
    private Visibility _steamBannerVisibility = Visibility.Collapsed;
    private string _steamBannerText = string.Empty;
    private string _lastOperation = string.Empty;
    private Visibility _lastOperationVisibility = Visibility.Collapsed;

    private string _runModeText = "契约模式";
    private Brush _runModeBrush = Lights.BlueText;
    private string _runModeDetail = "Cookie 由 NativeHost 直接写入 VRCVC 的 youtube_cookies.txt（Steam 版可用）";
    private string _runModeHint = "契约模式：Cookie 由本地写入程序写入 VRCVC 自己的 youtube_cookies.txt —— 自动更新、无需重启、原子写入。";

    /// <summary>
    /// 安装时是否同时注册用户级 DOTNET_STARTUP_HOOKS。默认 false：
    /// Steam 版单文件宿主不加载启动钩子，注册对它无效，反而会把插件注入用户启动的所有支持启动钩子的
    /// .NET 程序（插件会自行退出，但没必要）。
    /// </summary>
    private bool _registerStartupHookOnInstall;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        Overview = new OverviewPanel();
        Vrcvc = new VrcvcPanel();
        Plugin = new PluginPanel();
        Cookie = new CookiePanel();
        Live = new LivePanel();
        Native = new NativePanel();
        Backups = new BackupPanel();

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(false));
        InstallCommand = new AsyncRelayCommand(InstallHookAsync);
        UpdateCommand = new AsyncRelayCommand(InstallHookAsync);
        UninstallCommand = new AsyncRelayCommand(UninstallHookAsync);
        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync);
        BrowseInstallCommand = new AsyncRelayCommand(BrowseInstallAsync);
        RunSelfTestCommand = new AsyncRelayCommand(RunSelfTestAsync);
        CopyReportCommand = new RelayCommand(() => _dialogs.Copy(SelfTestReport));
        RegisterNativeCommand = new AsyncRelayCommand(RegisterNativeAsync);
        UnregisterNativeCommand = new AsyncRelayCommand(UnregisterNativeAsync);
        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => Backups.Selected != null);
        RefreshBackupsCommand = new AsyncRelayCommand(() => RefreshBackupsAsync(true));
        OpenBackupDirCommand = new RelayCommand(OpenBackupDir);
        RestartSteamCommand = new AsyncRelayCommand(RestartSteamAsync);
        OpenRootCommand = new RelayCommand(() => OpenPath(BridgePaths.Root));
        OpenLogDirCommand = new RelayCommand(() => OpenPath(BridgePaths.LogDir));
        OpenVrcvcDataDirCommand = new RelayCommand(() => OpenPath(BridgePaths.VrcvcDataDir));

        AsyncRelayCommand.ReportFailure = message => SetStatus(message, true);

        // 候选安装切换 → 重新按该目录探测版本/兼容性。
        Vrcvc.PropertyChanged += async (_, e) =>
        {
            if (_applying) return;
            if (e.PropertyName != nameof(VrcvcPanel.Selected)) return;

            var selected = Vrcvc.Selected;
            if (selected == null) return;

            _service.SelectInstall(selected.InstallDir);
            await RefreshAsync(true);
        };

        Backups.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BackupPanel.Selected))
                RestoreBackupCommand.RaiseCanExecuteChanged();
        };
    }

    // ---------------------------------------------------------------- 面板

    public OverviewPanel Overview { get; }
    public VrcvcPanel Vrcvc { get; }
    public PluginPanel Plugin { get; }
    public CookiePanel Cookie { get; }
    public LivePanel Live { get; }
    public NativePanel Native { get; }
    public BackupPanel Backups { get; }

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand ToggleEnabledCommand { get; }
    public AsyncRelayCommand BrowseInstallCommand { get; }
    public AsyncRelayCommand RunSelfTestCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public AsyncRelayCommand RegisterNativeCommand { get; }
    public AsyncRelayCommand UnregisterNativeCommand { get; }
    public AsyncRelayCommand CreateBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public AsyncRelayCommand RefreshBackupsCommand { get; }
    public RelayCommand OpenBackupDirCommand { get; }
    public AsyncRelayCommand RestartSteamCommand { get; }
    public RelayCommand OpenRootCommand { get; }
    public RelayCommand OpenLogDirCommand { get; }
    public RelayCommand OpenVrcvcDataDirCommand { get; }

    // ---------------------------------------------------------------- 绑定属性

    public string AppVersion => $"v{ManagerService.ManagerVersion}";
    public string RootPath => BridgePaths.Root;
    public string LogPath => BridgePaths.LogFile;
    public string VrcvcDataPath => BridgePaths.VrcvcDataDir;
    public string HookDllPath => HookInstallation.HookDllPath;
    public string BackupRootPath => BackupService.BackupRoot;
    public string NativeHostName => BridgePaths.NativeHostName;
    public string PipeName => BridgePaths.PipeName;
    public string StartupHooksVariable => BridgePaths.StartupHooksVariable;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public string SelfTestReport
    {
        get => _selfTestReport;
        private set => Set(ref _selfTestReport, value);
    }

    public Visibility SteamBannerVisibility
    {
        get => _steamBannerVisibility;
        private set => Set(ref _steamBannerVisibility, value);
    }

    public string SteamBannerText { get => _steamBannerText; private set => Set(ref _steamBannerText, value); }

    public string LastOperation { get => _lastOperation; private set => Set(ref _lastOperation, value); }

    public Visibility LastOperationVisibility
    {
        get => _lastOperationVisibility;
        private set => Set(ref _lastOperationVisibility, value);
    }

    /// <summary>运行模式：进程内模式（Hook 心跳新鲜）/ 契约模式（其它一切情况，Steam 版的正常工作方式）。</summary>
    public string RunModeText { get => _runModeText; private set => Set(ref _runModeText, value); }
    public Brush RunModeBrush { get => _runModeBrush; private set => Set(ref _runModeBrush, value); }
    public string RunModeDetail { get => _runModeDetail; private set => Set(ref _runModeDetail, value); }
    public string RunModeHint { get => _runModeHint; private set => Set(ref _runModeHint, value); }

    public bool RegisterStartupHookOnInstall
    {
        get => _registerStartupHookOnInstall;
        set => Set(ref _registerStartupHookOnInstall, value);
    }

    public string StartupHookCheckboxHint { get; } =
        "默认不勾选。Steam 版为单文件自包含发布，宿主已关闭 CoreCLR 启动钩子（System.StartupHookProvider.IsSupported=false），" +
        "注册该变量对它没有作用；而用户级 DOTNET_STARTUP_HOOKS 会把插件注入到你启动的所有支持启动钩子的 .NET 程序" +
        "（插件会自行退出，但没必要）。只有自编译 fork / 框架依赖宿主才需要勾选。";

    // ---------------------------------------------------------------- 刷新

    public async Task RefreshAsync(bool silent)
    {
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var snapshot = await Task.Run(() => _service.Refresh()).ConfigureAwait(true);
            Apply(snapshot);

            if (!silent) SetStatus("状态已刷新。", false);
        }
        catch (Exception ex)
        {
            SetStatus($"刷新状态失败：{ex.GetType().Name} - {ex.Message}", true);
            BridgeLog.Error("manager", "刷新状态失败", ex);
        }
        finally
        {
            _refreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private void Apply(ManagerSnapshot snapshot)
    {
        _snapshot = snapshot;
        _applying = true;
        try
        {
            Overview.Apply(snapshot);
            Vrcvc.Apply(snapshot);
            Plugin.Apply(snapshot);
            Cookie.Apply(snapshot);
            Live.Apply(snapshot);
            ApplyRunMode(snapshot);

            var (extensionId, firefoxId) = _service.ReadExtensionIdentity();
            Native.Apply(snapshot, extensionId, firefoxId);

            ApplySteamBanner(snapshot.Steam, snapshot.Live.Fresh);
        }
        catch (Exception ex)
        {
            SetStatus($"界面状态更新失败：{ex.GetType().Name} - {ex.Message}", true);
        }
        finally
        {
            _applying = false;
        }

        ToggleEnabledCommand.RaiseCanExecuteChanged();
        RestoreBackupCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 运行模式判定（lead 的架构结论）：
    /// HookStatus.Read()?.IsFresh == true → 进程内模式；否则一律是契约模式。
    /// 契约模式不是降级状态，它是 Steam 版（单文件自包含宿主，已禁用 CoreCLR 启动钩子）的正常工作方式。
    /// </summary>
    private void ApplyRunMode(ManagerSnapshot snapshot)
    {
        var inProcess = snapshot.Live.Fresh;

        if (inProcess)
        {
            RunModeText = "进程内模式";
            RunModeBrush = Lights.GreenText;
            RunModeDetail = $"Hook 已注入 VRCVC 进程（PID {snapshot.Live.Pid}），由进程内插件同步 Cookie。";
            RunModeHint = "进程内模式：Hook 已注入 VRCVC 进程，vault 变化会被进程内插件立即同步到 Cookie 文件。";
        }
        else
        {
            RunModeText = "契约模式";
            RunModeBrush = Lights.BlueText;
            RunModeDetail = "Cookie 由本地写入程序（NativeHost）直接写入 VRCVC 的 youtube_cookies.txt。";
            RunModeHint = "契约模式运行中（进程内注入不适用于单文件发布）—— Cookie 由 NativeHost 写入 VRCVC 自己的 youtube_cookies.txt，自动更新、无需重启、原子写入。";
        }
    }

    /// <summary>需求 14：Steam 早于插件安装时间启动 → 黄色横幅 + 重启按钮（按钮必须二次确认）。</summary>
    private void ApplySteamBanner(SteamStatus steam, bool inProcess)
    {
        if (!steam.EnvMayBeStale)
        {
            SteamBannerVisibility = Visibility.Collapsed;
            SteamBannerText = string.Empty;
            return;
        }

        var suffix = inProcess
            ? string.Empty
            : "（注：当前是契约模式，Cookie 生效并不依赖这个环境变量，重启 Steam 只是为了让新进程读到最新值。）";

        SteamBannerText =
            "需要重启 Steam 一次 —— Steam 早于插件安装时间启动，" +
            $"它（以及它启动的 VRCVideoCacher）可能仍持有旧的 {BridgePaths.StartupHooksVariable} 环境变量。" +
            $"（Steam 启动于 {Lights.Time(steam.SteamStartedUtc)}，插件安装于 {Lights.Time(steam.InstalledUtc)}）" +
            suffix;
        SteamBannerVisibility = Visibility.Visible;
    }

    private async Task RefreshBackupsAsync(bool report)
    {
        try
        {
            var entries = await Task.Run(() => _backupService.List()).ConfigureAwait(true);
            Backups.Apply(entries);
            if (report) SetStatus($"已扫描备份：{entries.Count} 份。", false);
        }
        catch (Exception ex)
        {
            SetStatus($"扫描备份失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- 插件动作

    private async Task InstallHookAsync()
    {
        try
        {
            var register = RegisterStartupHookOnInstall;
            var result = await Task.Run(() => _service.InstallHook(register)).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"安装失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private async Task UninstallHookAsync()
    {
        if (!_dialogs.Confirm("卸载插件",
                "将删除桥接目录下的 CookieBridge.Hook.dll、install.json，" +
                $"并从用户级 {BridgePaths.StartupHooksVariable} 中移除本插件条目。\n\n" +
                "VRCVideoCacher 的安装目录不会被改动；Cookie 文件与 vault 会保留。\n\n确定卸载吗？",
                "卸载", "取消"))
            return;

        try
        {
            var result = await Task.Run(() => _service.UninstallHook()).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"卸载失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private async Task ToggleEnabledAsync()
    {
        try
        {
            var currentlyEnabled = _snapshot?.Plugin.Enabled ?? HookInstallation.IsEnabled;
            var result = await Task.Run(() => _service.SetHookEnabled(!currentlyEnabled)).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"切换启用状态失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private async Task BrowseInstallAsync()
    {
        try
        {
            var initial = _snapshot?.Selected?.InstallDir ?? _service.ManualInstallDir;
            if (!_dialogs.PickFolder("选择 VRCVideoCacher 安装目录（包含 VRCVideoCacher.exe）", initial, out var path))
                return;

            _service.ManualInstallDir = path;
            _service.SelectInstall(path);
            await RefreshAsync(true);

            var problem = _snapshot?.ManualDirProblem;
            SetStatus(problem ?? $"已选择安装目录：{path}", problem != null);
        }
        catch (Exception ex)
        {
            SetStatus($"选择目录失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- 自检

    private async Task RunSelfTestAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _service.Refresh()).ConfigureAwait(true);
            Apply(snapshot);

            var report = await Task.Run(() => _service.BuildSelfTestReport(snapshot)).ConfigureAwait(true);
            SelfTestReport = report;
            SetStatus("自检完成，报告可复制。", false);
        }
        catch (Exception ex)
        {
            SelfTestReport = $"自检失败：{ex.GetType().Name} - {ex.Message}";
            SetStatus("自检失败。", true);
        }

        await RefreshBackupsAsync(false);
    }

    // ---------------------------------------------------------------- Native Host

    private async Task RegisterNativeAsync()
    {
        try
        {
            var chromium = Native.ExtensionId;
            var firefox = Native.FirefoxId;

            var result = await Task.Run(() => _service.RegisterNativeHost(chromium, firefox)).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"注册失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private async Task UnregisterNativeAsync()
    {
        if (!_dialogs.Confirm("注销 Native Messaging Host",
                $"将从 HKCU 删除 {BridgePaths.NativeHostName} 的浏览器注册项（不影响浏览器本身与扩展）。\n\n确定注销吗？",
                "注销", "取消"))
            return;

        try
        {
            var result = await Task.Run(() => _service.UnregisterNativeHost()).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"注销失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- 备份 / 恢复

    private async Task CreateBackupAsync()
    {
        try
        {
            var installDir = _snapshot?.Selected?.InstallDir;
            var (result, _) = await Task.Run(() => _backupService.Create(installDir)).ConfigureAwait(true);

            ShowOperation(result);
            await RefreshBackupsAsync(false);
        }
        catch (Exception ex)
        {
            SetStatus($"备份失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private async Task RestoreBackupAsync()
    {
        var selected = Backups.Selected;
        if (selected == null)
        {
            SetStatus("请先在列表里选择一份备份。", true);
            return;
        }

        // 需求 11：恢复前二次确认（默认“取消”）。
        if (!_dialogs.Confirm("从备份恢复",
                $"将把以下内容恢复到当前状态：\n\n{selected.Display}\n目录：{selected.Directory}\n\n" +
                $"1) VRCVC 的 Cookie 文件 {CookieTargetWriter.TargetPath}\n" +
                $"2) 用户级 {BridgePaths.StartupHooksVariable}\n" +
                "3) 插件安装记录 / 启用状态\n\n" +
                "当前状态会被覆盖（Cookie 文件会先留一份 .cookiebridge.bak）。\n确定恢复吗？",
                "恢复", "取消"))
            return;

        try
        {
            var result = await Task.Run(() => _backupService.Restore(selected.Directory)).ConfigureAwait(true);
            ShowOperation(result);
            await RefreshAsync(true);
            await RefreshBackupsAsync(false);
        }
        catch (Exception ex)
        {
            SetStatus($"恢复失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    private void OpenBackupDir()
    {
        var result = _service.OpenDirectory(BackupService.BackupRoot);
        SetStatus(result.Message, !result.Success);
    }

    // ---------------------------------------------------------------- Steam

    private async Task RestartSteamAsync()
    {
        // 需求 14：必须弹确认框，明确说明会关闭 Steam；默认“取消”。
        if (!_dialogs.Confirm("重启 Steam",
                "此操作会先用 steam -shutdown 关闭 Steam（正在运行的游戏与 VRCVideoCacher 会一起退出），" +
                "等它完全退出后再自动重新启动。\n\n" +
                $"这样新启动的 VRCVideoCacher 才能继承最新的 {BridgePaths.StartupHooksVariable}。\n\n" +
                "确定现在重启 Steam 吗？",
                "重启 Steam", "取消"))
            return;

        SetStatus("正在关闭 Steam…", false);
        try
        {
            var result = await _service.RestartSteamAsync().ConfigureAwait(true);
            ShowOperation(result);
            await Task.Delay(3000).ConfigureAwait(true);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus($"重启 Steam 失败：{ex.GetType().Name} - {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- 工具

    private void OpenPath(string path)
    {
        var result = _service.OpenDirectory(path);
        SetStatus(result.Message, !result.Success);
    }

    private void ShowOperation(OperationResult result)
    {
        SetStatus(result.Message.Split('\n')[0], !result.Success);
        LastOperation = result.Message;
        LastOperationVisibility = string.IsNullOrWhiteSpace(result.Message) ? Visibility.Collapsed : Visibility.Visible;

        if (result.Success) BridgeLog.Info("manager", result.Message.Split('\n')[0]);
        else BridgeLog.Warn("manager", result.Message.Split('\n')[0]);
    }

    public void SetStatus(string text, bool isError)
    {
        StatusText = text;
        StatusBrush = isError ? Lights.RedText : Lights.GrayText;
    }

    /// <summary>窗口加载后的一次初始化（状态 + 备份列表）。</summary>
    public async Task InitializeAsync()
    {
        await RefreshAsync(true);
        await RefreshBackupsAsync(false);
        SetStatus("就绪。核心状态来自 CookieBridge.Core，界面只做展示与触发。", false);
    }
}
