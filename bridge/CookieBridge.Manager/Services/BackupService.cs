using System.IO;
using System.Text;
using CookieBridge.Core;

namespace CookieBridge.Manager.Services;

/// <summary>备份目录里的一个快照。</summary>
public sealed class BackupEntry
{
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }
    public bool HasCookiesFile { get; init; }
    public bool HasInstallManifest { get; init; }
    public bool HasHookDll { get; init; }
    public string? EnvValue { get; init; }
    public bool WasDisabled { get; init; }
    public int? CookieCount { get; init; }

    public string Display
    {
        get
        {
            var parts = new List<string> { Name };
            if (CreatedUtc.HasValue) parts.Add(CreatedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            parts.Add(HasCookiesFile ? "含 Cookie 文件" : "无 Cookie 文件");
            if (CookieCount.HasValue) parts.Add($"{CookieCount} 条");
            parts.Add(HasInstallManifest ? "含 install.json" : "无 install.json");
            if (HasHookDll) parts.Add("含 Hook DLL");
            if (WasDisabled) parts.Add("当时禁用中");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 备份 / 恢复。
///
/// ⚠️ 重要说明（同时展示在 UI 上）：本插件**从不修改 VRCVideoCacher 的安装目录**，
/// 所以这里备份的也不是“VRCVC 安装目录”。备份内容是桥接自己可能改动的那几处状态：
///   1. VRCVC 的 Cookie 文件 %APPDATA%\VRCVideoCacher\youtube_cookies.txt
///   2. 用户级环境变量 DOTNET_STARTUP_HOOKS 的当前值
///   3. 插件安装记录 install.json（以及可选的 Hook DLL 副本）
///   4. 备份当时的 disabled.flag 状态（写进 manifest.json）
/// 备份目录位于 %LOCALAPPDATA%\VRCVideoCacher-CookieBridge\backups\&lt;时间戳&gt;。
/// 注意备份里的 youtube_cookies.txt 是**明文**登录 Cookie，请自行保管。
/// </summary>
public sealed class BackupService
{
    public const string ManifestFileName = "manifest.json";
    public const string CookiesFileName = "youtube_cookies.txt";
    public const string EnvFileName = "startup-hooks.txt";
    public const string InstallManifestFileName = "install.json";
    public const string HookDllFileName = "CookieBridge.Hook.dll";

    /// <summary>%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\backups</summary>
    public static string BackupRoot => Path.Combine(BridgePaths.Root, "backups");

    /// <summary>备份的元数据（不含任何 Cookie 内容）。</summary>
    public sealed class BackupManifest
    {
        public int FormatVersion { get; set; } = 1;
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? ManagerVersion { get; set; }
        public string? TargetCookiePath { get; set; }
        public bool HadCookiesFile { get; set; }
        public bool HadInstallManifest { get; set; }
        public bool HadHookDll { get; set; }
        public bool WasDisabled { get; set; }
        public string? EnvValue { get; set; }
        public int CookieCount { get; set; }
        public bool LoginCookiePresent { get; set; }
        public string? VrcvcInstallDir { get; set; }
        public string? Note { get; set; }
    }

    // ---------------------------------------------------------------- 列举

    public IReadOnlyList<BackupEntry> List()
    {
        var list = new List<BackupEntry>();

        try
        {
            if (!Directory.Exists(BackupRoot)) return list;

            foreach (var dir in Directory.GetDirectories(BackupRoot))
            {
                try
                {
                    var manifestPath = Path.Combine(dir, ManifestFileName);
                    BackupManifest? manifest = null;
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            manifest = BridgeJson.Deserialize<BackupManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                        }
                        catch
                        {
                            manifest = null;
                        }
                    }

                    var cookiesPath = Path.Combine(dir, CookiesFileName);
                    var cookiesText = File.Exists(cookiesPath) ? AtomicFile.TryReadAllText(cookiesPath) : null;
                    var cookieCount = cookiesText != null
                        ? NetscapeCookieFile.Summarize(cookiesText).CookieCount
                        : (int?)null;

                    list.Add(new BackupEntry
                    {
                        Directory = dir,
                        Name = Path.GetFileName(dir),
                        CreatedUtc = manifest?.CreatedUtc ?? Directory.GetCreationTimeUtc(dir),
                        HasCookiesFile = File.Exists(cookiesPath),
                        HasInstallManifest = File.Exists(Path.Combine(dir, InstallManifestFileName)),
                        HasHookDll = File.Exists(Path.Combine(dir, HookDllFileName)),
                        EnvValue = manifest?.EnvValue,
                        WasDisabled = manifest?.WasDisabled ?? false,
                        CookieCount = cookieCount,
                    });
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("backup", $"读取备份 {dir} 失败: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("backup", "列举备份失败", ex);
        }

        return list
            .OrderByDescending(e => e.CreatedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- 创建

    /// <summary>创建一份快照，返回结果 + 备份目录（失败时目录为 null）。</summary>
    public (OperationResult Result, string? Directory) Create(string? vrcvcInstallDir)
    {
        string? directory = null;
        var log = new StringBuilder();

        try
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(BackupRoot, stamp);

            // 同一秒内重复备份：加后缀避免覆盖。
            var suffix = 1;
            while (Directory.Exists(dir))
                dir = Path.Combine(BackupRoot, $"{stamp}-{suffix++}");

            Directory.CreateDirectory(dir);
            directory = dir;

            var manifest = new BackupManifest
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                ManagerVersion = ManagerService.ManagerVersion,
                TargetCookiePath = CookieTargetWriter.TargetPath,
                VrcvcInstallDir = vrcvcInstallDir,
                Note = "本插件不修改 VRCVC 安装目录；此快照只包含 Cookie 文件、DOTNET_STARTUP_HOOKS 与安装记录。",
                WasDisabled = !HookInstallation.IsEnabled,
            };

            // 1) Cookie 文件（明文，含登录凭证）
            try
            {
                var target = CookieTargetWriter.TargetPath;
                if (File.Exists(target))
                {
                    File.Copy(target, Path.Combine(dir, CookiesFileName), overwrite: true);
                    manifest.HadCookiesFile = true;

                    var text = AtomicFile.TryReadAllText(target);
                    if (text != null)
                    {
                        var summary = NetscapeCookieFile.Summarize(text);
                        manifest.CookieCount = summary.CookieCount;
                        manifest.LoginCookiePresent = summary.LoginCookiePresent;
                    }

                    log.AppendLine($"· Cookie 文件已备份（{manifest.CookieCount} 条）");
                }
                else
                {
                    log.AppendLine("· Cookie 文件不存在，跳过");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"· Cookie 文件备份失败：{ex.GetType().Name} - {ex.Message}");
            }

            // 2) DOTNET_STARTUP_HOOKS 当前值
            try
            {
                var env = HookInstallation.ReadUserEnv();
                manifest.EnvValue = env;
                AtomicFile.WriteAllText(Path.Combine(dir, EnvFileName),
                    string.IsNullOrEmpty(env)
                        ? "# DOTNET_STARTUP_HOOKS 当时未设置（用户级）\n"
                        : env + "\n");
                log.AppendLine(string.IsNullOrEmpty(env) ? "· DOTNET_STARTUP_HOOKS 为空（已记录）" : "· DOTNET_STARTUP_HOOKS 已记录");
            }
            catch (Exception ex)
            {
                log.AppendLine($"· DOTNET_STARTUP_HOOKS 备份失败：{ex.GetType().Name} - {ex.Message}");
            }

            // 3) install.json
            try
            {
                if (File.Exists(BridgePaths.InstallManifest))
                {
                    File.Copy(BridgePaths.InstallManifest, Path.Combine(dir, InstallManifestFileName), overwrite: true);
                    manifest.HadInstallManifest = true;
                    log.AppendLine("· install.json 已备份");
                }
                else
                {
                    log.AppendLine("· install.json 不存在（插件未安装），跳过");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"· install.json 备份失败：{ex.GetType().Name} - {ex.Message}");
            }

            // 4) Hook DLL 副本：恢复时用它走 HookInstallation.Install，能一次性重建 DLL + 清单 + 环境变量
            try
            {
                if (File.Exists(HookInstallation.HookDllPath))
                {
                    File.Copy(HookInstallation.HookDllPath, Path.Combine(dir, HookDllFileName), overwrite: true);
                    manifest.HadHookDll = true;
                    log.AppendLine("· Hook DLL 副本已备份");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"· Hook DLL 备份失败：{ex.GetType().Name} - {ex.Message}");
            }

            AtomicFile.WriteAllText(Path.Combine(dir, ManifestFileName), BridgeJson.Serialize(manifest));
            BridgeLog.Info("backup", $"备份已创建: {dir}");

            return (OperationResult.Ok($"备份已创建：\n{dir}\n\n{log}"), dir);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("backup", "创建备份失败", ex);
            return (OperationResult.Fail($"创建备份失败：{ex.GetType().Name} - {ex.Message}" +
                                         ManagerService.WriteFailureHint(ex) + $"\n{log}"), directory);
        }
    }

    // ---------------------------------------------------------------- 恢复

    /// <summary>
    /// 从备份恢复。调用方必须先做二次确认。
    /// Cookie 文件走 <see cref="CookieTargetWriter.Write"/>（原子写 + 互斥量），
    /// 环境变量走 <see cref="HookInstallation.RegisterEnv"/> / <see cref="HookInstallation.RemoveEnv"/>，
    /// 插件本体走 <see cref="HookInstallation.Install"/> —— 全部是 Core 公开 API。
    /// </summary>
    public OperationResult Restore(string backupDirectory)
    {
        var log = new StringBuilder();

        try
        {
            if (!Directory.Exists(backupDirectory))
                return OperationResult.Fail($"备份目录不存在：{backupDirectory}");

            BackupManifest? manifest = null;
            var manifestPath = Path.Combine(backupDirectory, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    manifest = BridgeJson.Deserialize<BackupManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    log.AppendLine($"· manifest.json 无法解析（{ex.GetType().Name}），按文件存在性恢复");
                }
            }

            // ---- 1) Cookie 文件 ----
            var cookiesPath = Path.Combine(backupDirectory, CookiesFileName);
            if (File.Exists(cookiesPath))
            {
                try
                {
                    var text = AtomicFile.TryReadAllText(cookiesPath);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        log.AppendLine("· Cookie 文件为空，跳过");
                    }
                    else
                    {
                        var outcome = CookieTargetWriter.Write(text, out var detail,
                            alreadyLocked: false, lockTimeout: TimeSpan.FromSeconds(5),
                            backupPath: CookieTargetWriter.DefaultBackupPath);
                        var summary = NetscapeCookieFile.Summarize(text);
                        log.AppendLine(CookieTargetWriter.Describe(outcome) == "已写入"
                            ? $"· Cookie 文件已恢复（{summary.CookieCount} 条，登录 Cookie={(summary.LoginCookiePresent ? "有" : "无")}）"
                            : $"· Cookie 文件恢复：{CookieTargetWriter.Describe(outcome)}（{detail}）");
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine($"· Cookie 文件恢复失败：{ex.GetType().Name} - {ex.Message}");
                }
            }
            else
            {
                log.AppendLine("· 该备份不含 Cookie 文件，跳过");
            }

            // ---- 2) 插件本体 / 安装记录 / 环境变量 ----
            var hookDllBackup = Path.Combine(backupDirectory, HookDllFileName);
            var envInBackup = manifest?.EnvValue;

            if (File.Exists(hookDllBackup))
            {
                // 备份里的环境变量若包含本插件，说明当时是“注册过启动钩子”的状态，恢复时一并还原；
                // 否则保持新默认（不写 DOTNET_STARTUP_HOOKS）。
                var envHadHook = !string.IsNullOrWhiteSpace(envInBackup) &&
                                 envInBackup!.Contains(HookDllFileName, StringComparison.OrdinalIgnoreCase);

                var ok = HookInstallation.Install(hookDllBackup, out var message, registerStartupHook: envHadHook);
                log.AppendLine(ok
                    ? $"· 插件已按备份重装（DLL + install.json{(envHadHook ? " + DOTNET_STARTUP_HOOKS" : "，未注册启动钩子")}）"
                    : $"· 插件重装失败：{message}");

                if (!ok && File.Exists(Path.Combine(backupDirectory, InstallManifestFileName)))
                {
                    // 兜底：Core 没有“写 install.json”的公开 API，这里按备份直接还原该记录文件。
                    try
                    {
                        File.Copy(Path.Combine(backupDirectory, InstallManifestFileName),
                            BridgePaths.InstallManifest, overwrite: true);
                        log.AppendLine("· install.json 已按备份直接还原（兜底路径）");
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"· install.json 还原失败：{ex.GetType().Name}");
                    }
                }
            }
            else if (string.IsNullOrWhiteSpace(envInBackup))
            {
                // 备份时没有插件、环境变量也为空 → 清理掉本插件条目即可。
                try
                {
                    HookInstallation.RemoveEnv(out var envMessage);
                    log.AppendLine("· DOTNET_STARTUP_HOOKS 已恢复为“不含本插件”的状态" +
                                   (string.IsNullOrEmpty(envMessage) ? "" : $"（{envMessage}）"));
                }
                catch (Exception ex)
                {
                    log.AppendLine($"· 环境变量恢复失败：{ex.GetType().Name}");
                }
            }
            else
            {
                log.AppendLine("· 该备份不含 Hook DLL，插件本体未改动；环境变量按当前安装态保持不变");
            }

            // ---- 3) 禁用标记 ----
            try
            {
                if (manifest != null)
                {
                    HookInstallation.SetEnabled(!manifest.WasDisabled);
                    log.AppendLine(manifest.WasDisabled ? "· 已恢复为“禁用”状态" : "· 已恢复为“启用”状态");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"· 启用状态恢复失败：{ex.GetType().Name}");
            }

            BridgeLog.Info("backup", $"已从备份恢复: {backupDirectory}");
            return OperationResult.Ok($"已从备份恢复：\n{backupDirectory}\n\n{log}");
        }
        catch (Exception ex)
        {
            BridgeLog.Error("backup", "恢复失败", ex);
            return OperationResult.Fail($"恢复失败：{ex.GetType().Name} - {ex.Message}\n{log}");
        }
    }

    /// <summary>UI 上显示的备份说明（必须讲清楚：插件从不改 VRCVC 安装目录）。</summary>
    public static string Explanation =>
        "备份内容（不会碰 VRCVideoCacher 的安装目录，插件从不修改那里）：\n" +
        "  1. VRCVC 的 Cookie 文件 %APPDATA%\\VRCVideoCacher\\youtube_cookies.txt（明文，注意保管）\n" +
        "  2. 用户级环境变量 DOTNET_STARTUP_HOOKS 的当前值\n" +
        "  3. 插件安装记录 install.json（附带 Hook DLL 副本，便于一键还原安装态）\n" +
        "  4. 备份当时的启用/禁用状态\n\n" +
        "恢复时 Cookie 文件走 Core 的原子写 + 互斥量；插件与环境变量走 HookInstallation 的公开 API。";

    public OperationResult OpenBackupRoot()
    {
        try
        {
            Directory.CreateDirectory(BackupRoot);
        }
        catch
        {
            // 打不开也无所谓，下面 explorer 会报错
        }

        return OperationResult.Ok(BackupRoot);
    }
}
