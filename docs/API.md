# CookieBridge.Core 公共 API（供 Manager / Tests 使用）

命名空间 `CookieBridge.Core`。所有类型都是 public，无依赖注入、无配置对象，直接调用。

## 路径 `BridgePaths`

| 成员 | 说明 |
| --- | --- |
| `Root` | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge`；`VRCCB_ROOT` 环境变量可覆盖（测试用） |
| `VaultFile` | `vault.json`（DPAPI 密文） |
| `LogFile` / `LogDir` | 桥接日志 |
| `StateDir`, `HookStatusFile`, `PendingSyncMarker`, `DisabledFlag`, `InstallManifest` | 状态文件 |
| `HookDir` | 插件 DLL 目录 |
| `VrcvcDataDir` | `%APPDATA%\VRCVideoCacher`；`VRCCB_VRCVC_DATA` 可覆盖 |
| `VrcvcCookieFile` | `%APPDATA%\VRCVideoCacher\youtube_cookies.txt` ← 与 VRCVC 的唯一契约点 |
| `VrcvcConfigFile`, `VrcvcLogDir` | 只读检测用 |
| `PipeName` | `VRCVideoCacherCookieBridge`（Hook 控制管道） |
| `NativeHostName` | `com.vrcvideocacher.cookiebridge` |
| `StartupHooksVariable` | `DOTNET_STARTUP_HOOKS` |
| `EnsureDirectories()` | 创建 Root 及子目录 |

## 日志 `BridgeLog`

`Info/Warn/Error/Debug(string source, string message[, Exception ex])`、`Enabled`、`LogPath`、`Format(...)`。
`BridgeLog.LineWritten` 事件（每写一行触发一次，供 UI 订阅）。**禁止把 Cookie 内容传进来。**

## Vault `CookieVault`

```csharp
VaultLoadResult Save(string netscapeText, VaultMetadata metadata);   // 自动规范化 + DPAPI 加密 + 原子写
VaultLoadResult Load();                                             // 解密 + 校验 sha256 + 有效性
VaultDocument?  ReadDocument();                                     // 只读元数据（不解密）
DateTimeOffset? LastUpdatedUtc();
void            Delete();
```
`VaultLoadResult{ Success, CookiesText, Document, Summary, Error, UpdatedUtc }`
`VaultMetadata{ SourceBrowser, ExtensionVersion, SourceHost, CookieCount, LoginCookiePresent, EarliestExpiryUtc, LatestExpiryUtc, LastError }`
`VaultDocument{ FormatVersion, CreatedUtc, UpdatedUtc, Protection, PayloadSha256, Payload, Metadata }`

## Netscape `NetscapeCookieFile`

`Parse`, `Canonicalize`, `Summarize` → `CookieSummary{CookieCount, LoginCookiePresent, EarliestExpiryUtc, LatestExpiryUtc, HasYoutubeDomain, Sha256}`, `IsValidForVrcvc`, `IsUsable`, `Sha256Hex`, `Header`。

## 目标文件 `CookieTargetWriter`

```csharp
string TargetPath { get; }
TargetFileState ReadState();                                       // Exists/Readable/Valid/CookieCount/LastWriteUtc/SizeBytes/Error
TargetWriteOutcome Write(string text, out string detail, bool alreadyLocked = false,
                         TimeSpan? lockTimeout = null, string? backupPath = null);
string DefaultBackupPath { get; }
string Describe(TargetWriteOutcome outcome);
```

## 进程 / 安装检测

* `VrcvcProcessState.IsRunning()`, `RunningPid()`, `FindRunning()`
* `VrcvcLocator.Discover(string? userOverrideDir=null)` → `VrcvcInstall{ExePath, InstallDir, DiscoverySource, LooksLikeSteamInstall}`
* `VrcvcLocator.SteamLibraries()`, `ReadSteamPath()`, `ReadLibraryFolders()`, `Validate(dir)`
* `VrcvcLocator.DetectVersion(installDir, runningAssemblyVersion)` → `VrcvcVersionInfo{Version, Source, FromRunningProcess, Known}`
* `VrcvcLocator.ReadVersionFromLogs()`

## 兼容性 `CompatibilityMatrix`

`VerifiedVersion`(2026.10.29)、`MinimumSupported`(2025.1.1)、`Parse(string?)`、`Evaluate(string?)` → `CompatReport{Level, Version, Headline, Notes, HookAllowed, LevelLabel}`；`CompatLevel{Verified, Supported, UntestedNewer, UntestedOlder, Unknown}`。

## 插件安装 `HookInstallation`

```csharp
string HookDllPath { get; }                       // <HookDir>\CookieBridge.Hook.dll
HookInstallManifest? ReadManifest();
HookInstallState Inspect();                       // Installed/Enabled/EnvRegistered/DllPresent/DllHashMatches/HookVersion/Problems
bool Install(string sourceHookDll, out string message);
bool Uninstall(out string message);
void SetEnabled(bool enabled);
bool IsEnabled { get; }
string? ReadUserEnv();
bool RegisterEnv(string hookDll, out string message);   // 追加，不覆盖他人 hook
bool RemoveEnv(out string message);
void BroadcastEnvironmentChange();
string Sha256File(string path);
```

## Native Messaging `NativeHostRegistration`

`Targets`（Chrome/Chromium/Edge/Brave/Vivaldi/Firefox）、
`Register(hostExePath, chromiumExtensionIds, firefoxExtensionIds, out message)`、
`Unregister(out message)`、`GetRegisteredTargets()`、
`ChromiumManifestPath`、`FirefoxManifestPath`。

## 协议与状态

* `NativeMessageProtocol.ReadMessage(Stream)` / `WriteMessage(Stream, string)`
* `NativeMessageHandler.HandleJson(string)` → `NativeMessageResponse{Ok, VaultUpdated, CookieCount, TargetWritten, Applied, Message, Error, VaultUpdatedUtc, VrcvcRunning, HookPresent}`
* `HookStatus`（`Pid, StartedUtc, LastHeartbeatUtc, LastSyncUtc, VrcvcVersion, HookVersion, LastSyncOutcome, SyncCount, LastError, VaultWatching, Layers, IsFresh`）+ `Read()/Write()/Clear()`
* `HookPipeCommands{Status, SyncNow, Reload}`、`HookPipeCommand`、`HookPipeReply`
* `CookieWriteLock.TryAcquire(TimeSpan)`、`AtomicFile.WriteAllText/TryReadAllText`、`SecretProtector.Protect/Unprotect`、`FileAcl.TryRestrictToCurrentUser`

## 状态机约定（Manager 用）

* 插件“已安装” = `Inspect().Installed && DllPresent`。
* 插件“已启用” = `IsEnabled`（`disabled.flag` 不存在）。
* 插件“正在 VRCVC 内运行” = `HookStatus.Read()?.IsFresh == true`（90 秒心跳 + 进程存活）。
* VRCVC “是否已具备新环境变量” = 安装时间（`InstallManifest.InstalledUtc`）与 `Process.StartTime`（steam.exe）比较。
